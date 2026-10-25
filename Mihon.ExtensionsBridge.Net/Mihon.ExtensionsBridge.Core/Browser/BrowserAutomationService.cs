using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Mihon.ExtensionsBridge.Models.Helpers;

namespace Mihon.ExtensionsBridge.Core.Browser
{
    /// <summary>
    /// Owns provider selection, session pooling, the concurrency cap and lifecycle for the
    /// .NET-hosted browser subsystem (plan §4.2 / §12.3).
    ///
    /// Semantics preserved from the old Java code:
    /// <list type="bullet">
    /// <item><c>RendererGate.reserve/release</c> -> <see cref="SemaphoreSlim"/> page slots.</item>
    /// <item><c>RendererGate.canSpawn()</c> -> <see cref="CapacityAvailable"/> advisory check.</item>
    /// <item><c>WebViewPool.acquire(host)</c> -> <see cref="GetOrCreateSessionAsync"/> keyed by host.</item>
    /// <item><c>WebViewPool.sweep()</c> -> a hosted background timer.</item>
    /// <item><c>RendererGate.stats()</c> -> <see cref="DescribeStats"/> diagnostics.</item>
    /// </list>
    /// </summary>
    public sealed class BrowserAutomationService : IBrowserAutomationService, IAsyncDisposable
    {
        private readonly IBrowserProviderRegistry _registry;
        private readonly ILogger<BrowserAutomationService> _logger;
        private readonly Func<BrowserAutomationPreferences>? _preferencesAccessor;
        private readonly IWorkingFolderStructure? _folder;
        private readonly IBrowserRuntimeInstaller[] _installers;

        /// <summary>
        /// Concurrency cap (the .NET replacement for RendererGate). Not readonly: warm-up replaces
        /// it because the effective value comes from the LOADED preferences, which are only
        /// available after WarmUpAsync resolves them — the constructor necessarily sees defaults.
        /// </summary>
        private volatile SemaphoreSlim _pageSlots;
        private readonly ConcurrentDictionary<string, Lazy<Task<PooledSession>>> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lifecycleLock = new();

        private volatile bool _initialized;
        private volatile bool _available;
        private volatile IBrowserAutomationProvider? _provider;
        private BrowserAutomationPreferences _preferences = new();

        /// <summary>
        /// Proxy configuration pushed from the host (the backend's SocksProxy settings). Kept
        /// SEPARATE from <see cref="BrowserAutomationPreferences"/> because the proxy group is
        /// shared with the JVM chain; the browser subsystem is just another consumer of it.
        ///
        /// Volatile because <see cref="BuildLaunchOptions"/> reads it per launch while
        /// <see cref="ApplyProxyPreferences"/> writes it on a settings push.
        /// </summary>
        private volatile SocksProxyPreferences _proxy = new();

        private Timer? _idleSweepTimer;

        // Diagnostics (mirrors RendererGate counters).
        private long _peak;
        private long _refused;
        private long _total;

        /// <summary>Requests currently holding a page slot (used when resizing the cap).</summary>
        private int _inFlight;

        public BrowserAutomationService(
            IBrowserProviderRegistry registry,
            ILogger<BrowserAutomationService> logger,
            IWorkingFolderStructure folder,
            Func<BrowserAutomationPreferences>? preferencesAccessor = null,
            IEnumerable<IBrowserRuntimeInstaller>? installers = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // REQUIRED, not optional: the browser install root (AndroidFolder) and cache root
            // (TempFolder) come from here, and there are no hardcoded fallbacks. Making it nullable
            // previously allowed a container/manual construction to omit it, which only surfaced
            // much later as "InstallRoot and CacheRoot are null" during install. Failing here turns
            // that into an immediate, obvious startup error.
            _folder = folder ?? throw new ArgumentNullException(
                nameof(folder),
                "BrowserAutomationService requires the host IWorkingFolderStructure " +
                "(AndroidFolder/TempFolder are the browser install and cache roots).");

            _preferencesAccessor = preferencesAccessor;
            _installers = (installers ?? []).ToArray();
            _pageSlots = new SemaphoreSlim(
                EffectiveConcurrency(_preferences.MaxConcurrency),
                EffectiveConcurrency(_preferences.MaxConcurrency));
        }

        /// <summary>Guards against a zero/negative cap from bad configuration (deadlocks otherwise).</summary>
        private static int EffectiveConcurrency(int configured) => configured > 0 ? configured : 4;

        /// <summary>
        /// Authoritative renderer/session slot reservation — the port of the old
        /// <c>RendererGate.reserve()</c>.
        ///
        /// The old gate was enforced inside the WebView provider (the factory for EVERY WebView),
        /// so it capped <b>all</b> browsers regardless of who created them. Live-DOM sessions
        /// created through <see cref="BrowserHostBridge"/> are exactly that: a browser per
        /// WebView. Routing them through this gate restores the original behaviour — without it,
        /// constructing N WebViews launched N full browser contexts (observed: seven for two
        /// providers).
        ///
        /// WAITS for a slot rather than refusing, because a WebView-requiring source has no
        /// fallback. Returns false only on timeout, which indicates a real stall.
        /// </summary>
        public bool TryReserveSessionSlot(TimeSpan timeout)
        {
            var bounded = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;

            // Deliberately NO _refused increment here: callers poll this in slices while also
            // looking for a reusable idle session, so a false return is normal, not a refusal.
            // The caller reports the final timeout.
            if (!_pageSlots.Wait(bounded))
            {
                return false;
            }

            Interlocked.Increment(ref _inFlight);
            return true;
        }

        /// <summary>Releases a slot taken by <see cref="TryReserveSessionSlot"/>.</summary>
        public void ReleaseSessionSlot()
        {
            Interlocked.Decrement(ref _inFlight);
            _pageSlots.Release();
        }

        /// <inheritdoc />
        public bool IsAvailable => _available && _provider is not null;

        /// <summary>
        /// Fast, non-reserving advisory check (the .NET replacement for
        /// <c>RendererGate.canSpawn()</c>). When false, callers should degrade to the direct
        /// network chain instead of issuing a fetch that would be refused.
        /// </summary>
        public bool CapacityAvailable => IsAvailable && _pageSlots.CurrentCount > 0;

        /// <inheritdoc />
        public async Task WarmUpAsync(CancellationToken ct = default)
        {
            var prefs = _preferencesAccessor?.Invoke();

            if (prefs is null && _folder is not null)
            {
                try
                {
                    var loaded = await _folder.LoadPreferencesAsync(ct).ConfigureAwait(false);
                    prefs = loaded?.Browser;

                    // Seed the proxy from the SAME on-disk preferences. The host's RPC
                    // SetPreferences push arrives LATER (after applicationSetup), so without this
                    // the first launches would egress directly even though a proxy is configured.
                    if (loaded?.SocksProxy is not null)
                    {
                        ApplyProxyPreferences(loaded.SocksProxy);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load browser preferences; using defaults.");
                }
            }

            prefs ??= new BrowserAutomationPreferences();

            if (!prefs.Enabled)
            {
                _available = false;
                _logger.LogInformation("Browser automation is disabled via settings (Browser.Enabled=false).");
                return;
            }

            // Normalise the cap ONCE so every consumer (the semaphore, DescribeStats, TrackPeak,
            // BuildLaunchOptions) agrees. Without this a configured 0/negative would leave the
            // semaphore at 4 while stats reported max=0.
            var desired = EffectiveConcurrency(prefs.MaxConcurrency);
            if (desired != prefs.MaxConcurrency)
            {
                _logger.LogWarning(
                    "Browser concurrency cap {Configured} is invalid; using {Effective}.",
                    prefs.MaxConcurrency,
                    desired);
                prefs.MaxConcurrency = desired;
            }

            lock (_lifecycleLock)
            {
                _preferences = prefs;

                // The effective cap is only known now (the constructor saw field defaults), so
                // replace the semaphore — preserving slots already held — unless nothing changed.
                var current = _pageSlots;
                if (desired != current.CurrentCount)
                {
                    if (desired > _inFlight)
                    {
                        _pageSlots = new SemaphoreSlim(desired - _inFlight, desired);
                        current.Dispose();
                        _logger.LogInformation("Browser concurrency cap set to {Max}.", desired);
                    }
                    else
                    {
                        // Cannot shrink below the in-flight count; re-applies on next restart.
                        _logger.LogWarning(
                            "Browser concurrency cap change to {Desired} skipped: {InFlight} request(s) " +
                            "in flight. Takes effect on restart.",
                            desired,
                            _inFlight);
                    }
                }
            }

            // Paths are NOT resolved or pushed here: every provider/installer receives the DI
            // IWorkingFolderStructure (single source of truth) and derives its own locations
            // (install = AndroidFolder/{Provider}, cache/profiles = TempFolder).
            _logger.LogInformation(
                "Browser paths: install={InstallRoot} cache={TempRoot}.",
                Path.Combine(_folder.AndroidFolder, BrowserRuntimeLayout.ToProviderFolder(prefs.Provider)),
                _folder.TempFolder);

            try
            {
                var provider = _registry.ResolveWithFallback(
                    prefs.Provider,
                    prefs.FallbackProviders);

                _provider = provider;
                _available = true;
                _logger.LogInformation(
                    "Browser automation provider resolved: {Provider} (capabilities={Capabilities}).",
                    provider.Name,
                    provider.Capabilities);

                // Ensure the runtime is present before first use, mirroring the jcefmaven install
                // behaviour (plan §5.4). Best-effort: failures degrade to the direct chain.
                await EnsureRuntimeInstalledAsync(provider.Name, prefs, ct).ConfigureAwait(false);

                StartIdleSweep();
            }
            catch (BrowserUnavailableException ex)
            {
                _available = false;
                _provider = null;
                // Graceful degradation: WebView-backed sources fall back to direct network.
                _logger.LogWarning(ex, "No browser automation provider available; WebView sources will degrade.");
                return;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<BrowserResponse> FetchAsync(BrowserRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAvailable || _pageSlots.CurrentCount == 0)
            {
                Interlocked.Increment(ref _refused);
                throw new BrowserUnavailableException("browser capacity exhausted");
            }

            await _pageSlots.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _inFlight);
            var pooled = (PooledSession?)null;
            try
            {
                TrackPeak();
                Interlocked.Increment(ref _total);
                pooled = await GetOrCreateSessionAsync(request.Url, ct).ConfigureAwait(false);
                var response = await pooled.Session.ExecuteAsync(request, ct).ConfigureAwait(false);
                return response;
            }
            finally
            {
                if (pooled is not null)
                {
                    pooled.Touch();
                }
                Interlocked.Decrement(ref _inFlight);
                _pageSlots.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IBrowserSession> OpenSessionAsync(
            BrowserLaunchOptions? options = null,
            CancellationToken ct = default)
        {
            if (!IsAvailable || _provider is null)
            {
                throw new BrowserUnavailableException("browser automation is not available");
            }

            var launch = options ?? BuildLaunchOptions();
            return await _provider.LaunchAsync(launch, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds launch options from the current preferences. Deliberately carries no directories:
        /// every path is derived from the DI <see cref="IWorkingFolderStructure"/> by the provider
        /// and its installer, so there is a single source of truth.
        /// </summary>
        public BrowserLaunchOptions BuildLaunchOptions()
        {
            var proxy = _proxy;

            // Normalise first: the UI accepts "192.168.1.5" and "http://192.168.1.5" alike.
            var host = ProxyEndpoint.NormalizeHost(proxy?.Host);

            // No usable proxy configured -> carry no proxy fields at all, so the browser egresses
            // directly exactly as it did before this feature existed.
            if (proxy is null || !proxy.Enabled || host.Length == 0 || proxy.Port <= 0)
            {
                return new BrowserLaunchOptions
                {
                    Headless = _preferences.Headless,
                    Persistent = _preferences.PoolEnabled,
                    Channel = _preferences.Channel,
                    UserAgent = _preferences.Stealth ? null : null,
                    MaxConcurrency = _preferences.MaxConcurrency,
                };
            }

            // trawl is HTTP-only ("http://host:8192", plain HTTP even for HTTPS CONNECT) and must
            // never be used as SOCKS; "socks5"/"socks4" select the legacy SOCKS paths.
            var scheme = ProxyEndpoint.ToUrlScheme(proxy.Scheme);

            return new BrowserLaunchOptions
            {
                Headless = _preferences.Headless,
                Persistent = _preferences.PoolEnabled,
                Channel = _preferences.Channel,
                UserAgent = _preferences.Stealth ? null : null,
                MaxConcurrency = _preferences.MaxConcurrency,
                ProxyUrl = $"{scheme}://{host}:{proxy.Port}",
                ProxyUsername = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
                ProxyPassword = string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password,
                CaCertSource = string.IsNullOrWhiteSpace(proxy.CaCertPath) ? null : proxy.CaCertPath,
                IgnoreTlsErrors = proxy.IgnoreTlsErrors,
            };
        }

        /// <summary>
        /// Applies proxy settings pushed by the host (see <c>BridgeManager.SetPreferencesAsync</c>).
        ///
        /// Without this the browser subsystem only ever saw preferences loaded at ITS warm-up,
        /// which runs BEFORE the host's RPC <c>SetPreferences</c> push — so a proxy saved in
        /// Settings never reached Patchright and every session egressed directly.
        ///
        /// A change DRAINS the pool: Chromium captures its proxy at launch and cannot hot-swap it,
        /// so pooled contexts are discarded and rebuilt on the next request (same contract as
        /// switching the JVM scheme, which needs a restart).
        /// </summary>
        public void ApplyProxyPreferences(SocksProxyPreferences? proxy)
        {
            var next = proxy ?? new SocksProxyPreferences();
            if (ProxyEquals(_proxy, next))
            {
                return;
            }

            _proxy = next;
            DrainPool();
            _logger.LogInformation(
                "Browser proxy settings changed (enabled={Enabled}, scheme={Scheme}, host={Host}:{Port}); " +
                "session pool drained, applies on next launch.",
                next.Enabled,
                next.Scheme,
                next.Host,
                next.Port);
        }

        private static bool ProxyEquals(SocksProxyPreferences a, SocksProxyPreferences b) =>
            a.Enabled == b.Enabled
            && a.Port == b.Port
            && a.IgnoreTlsErrors == b.IgnoreTlsErrors
            && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Username, b.Username, StringComparison.Ordinal)
            && string.Equals(a.Password, b.Password, StringComparison.Ordinal)
            && string.Equals(a.CaCertPath, b.CaCertPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Drops every pooled context so their captured proxy is no longer reused.</summary>
        private void DrainPool()
        {
            foreach (var kvp in _sessions.ToArray())
            {
                if (_sessions.TryRemove(kvp.Key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                {
                    _ = DisposePooledAsync(lazy.Value.Result);
                }
            }
        }

        /// <inheritdoc />
        public async Task ShutdownAsync()
        {
            _available = false;

            // Dispose pooled live-DOM sessions first: they hold slots, and the fetch sessions below
            // are awaited.
            try
            {
                LiveDomSessionsDisposer?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disposing pooled live-DOM sessions.");
            }

            _idleSweepTimer?.Dispose();
            _idleSweepTimer = null;

            var sessions = _sessions.Values.ToArray();
            _sessions.Clear();

            foreach (var lazy in sessions)
            {
                try
                {
                    if (!lazy.IsValueCreated)
                    {
                        continue;
                    }

                    var pooled = await lazy.Value.ConfigureAwait(false);
                    await pooled.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error while disposing a browser session during shutdown.");
                }
            }
        }

        public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

        /// <summary>Diagnostics string (mirrors <c>RendererGate.stats()</c>).</summary>
        public string DescribeStats() =>
            $"live={_inFlight} " +
            $"peak={Interlocked.Read(ref _peak)} " +
            $"max={_preferences.MaxConcurrency} " +
            $"refused={Interlocked.Read(ref _refused)} " +
            $"total={Interlocked.Read(ref _total)} " +
            $"fetchPool={_sessions.Count} " +
            $"idleWebViews={_liveDomSessions} " +
            $"provider={_provider?.Name ?? "none"}";

        /// <summary>
        /// Registered by <see cref="BrowserHostBridge"/> so shutdown also tears down pooled
        /// live-DOM sessions. Without this, sessions returned to the idle pool at destroy() would
        /// keep their browser contexts (and slots) alive for the lifetime of the process.
        /// </summary>
        public Action? LiveDomSessionsDisposer { get; set; }

        private long _liveDomSessions;

        /// <summary>
        /// Installs the resolved provider's runtime when <c>Browser.AutoInstall</c> is enabled.
        /// The installer derives every path from the DI <see cref="IWorkingFolderStructure"/>, so no
        /// directory is passed in. Best-effort: a failure logs and continues (the engine installs
        /// lazily on first launch, or the request degrades to the direct chain).
        /// </summary>
        private async Task EnsureRuntimeInstalledAsync(
            string providerName,
            BrowserAutomationPreferences prefs,
            CancellationToken ct)
        {
            if (!prefs.AutoInstall)
            {
                return;
            }

            var installer = _installers.FirstOrDefault(
                i => string.Equals(i.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
            if (installer is null)
            {
                return;
            }

            try
            {
                await installer
                    .EnsureInstalledAsync(force: false, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser runtime install failed for '{Provider}'; continuing.", providerName);
            }
        }

        private async Task<PooledSession> GetOrCreateSessionAsync(string url, CancellationToken ct)
        {
            var provider = _provider
                ?? throw new BrowserUnavailableException("browser automation is not available");

            // Keyed by host/profile so same-host requests reuse an isolated context (mirrors the
            // per-host WebViewPool semantics).
            var key = _preferences.PoolEnabled ? ResolvePoolKey(url) : Guid.NewGuid().ToString("N");

            var lazy = _sessions.GetOrAdd(
                key,
                _ => new Lazy<Task<PooledSession>>(async () =>
                {
                    var session = await provider.LaunchAsync(BuildLaunchOptions(), ct).ConfigureAwait(false);
                    return new PooledSession(key, session);
                }));

            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                // Failed launch: don't cache a faulted session.
                _sessions.TryRemove(key, out _);
                throw;
            }
        }

        private static string ResolvePoolKey(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
            return "default";
        }

        private void StartIdleSweep()
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(5_000, _preferences.IdleTimeoutMs / 4));
            _idleSweepTimer?.Dispose();
            _idleSweepTimer = new Timer(_ => Sweep(), null, interval, interval);
        }

        private void Sweep()
        {
            if (!_preferences.PoolEnabled)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(_preferences.IdleTimeoutMs);
            foreach (var kvp in _sessions.ToArray())
            {
                var lazy = kvp.Value;
                if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
                {
                    continue;
                }

                var pooled = lazy.Value.Result;
                if (pooled.InUse || pooled.LastUsedUtc > cutoff)
                {
                    continue;
                }

                if (_sessions.TryRemove(kvp.Key, out _))
                {
                    _logger.LogInformation(
                        "Evicting idle browser session host={Host} (idle since {Idle:O}).",
                        kvp.Key,
                        pooled.LastUsedUtc);
                    _ = DisposePooledAsync(pooled);
                }
            }
        }

        private async Task DisposePooledAsync(PooledSession pooled)
        {
            try
            {
                await pooled.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while evicting an idle browser session.");
            }
        }

        private void TrackPeak()
        {
            var live = _preferences.MaxConcurrency - _pageSlots.CurrentCount;
            long peak;
            while (live > (peak = Interlocked.Read(ref _peak)))
            {
                if (Interlocked.CompareExchange(ref _peak, live, peak) == peak)
                {
                    break;
                }
            }
        }

        /// <summary>Tracks LRU bookkeeping for a pooled session (mirrors WebViewPool.Entry).</summary>
        private sealed class PooledSession
        {
            private int _inUse;

            public PooledSession(string key, IBrowserSession session)
            {
                Key = key;
                Session = session;
                LastUsedUtc = DateTime.UtcNow;
                _inUse = 1;
            }

            public string Key { get; }

            public IBrowserSession Session { get; }

            public DateTime LastUsedUtc { get; private set; }

            public bool InUse => Volatile.Read(ref _inUse) != 0;

            public void Touch()
            {
                LastUsedUtc = DateTime.UtcNow;
                Volatile.Write(ref _inUse, 0);
            }

            public ValueTask DisposeAsync() => Session.DisposeAsync();
        }
    }
}
