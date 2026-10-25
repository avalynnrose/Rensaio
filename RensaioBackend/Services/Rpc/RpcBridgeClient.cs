using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Contracts.V1;
using RensaioBackend.Services.Rpc;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Backend-facing bridge implementation that forwards every operation to the
/// isolated Rensaio.RPC process over gRPC.
///
/// It implements the same abstractions the in-process bridge used
/// (<see cref="IBridgeManager"/>, <see cref="IExtensionManager"/>,
/// <see cref="IRepositoryManager"/>) and reuses the shared, IKVM-free model
/// types, so downstream services keep working with minimal changes.
///
/// Calls await the supervisor's readiness gate first, so a call issued during an
/// RPC restart blocks until the server is back online instead of failing.
/// </summary>
public sealed class RpcBridgeClient : IBridgeManager, IExtensionManager, IRepositoryManager, IExtensionRpcBridge, IDisposable
{
    private readonly IRpcReadinessGate _gate;
    private readonly IRpcProcessSupervisor _supervisor;
    private readonly ILogger<RpcBridgeClient> _logger;
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    private GrpcChannel? _channel;
    private int _channelPort;

    /// <summary>
    /// Bridge-readiness gate, distinct from the supervisor's process/listening gate.
    ///
    /// The supervisor's <see cref="IRpcReadinessGate.Ready"/> completes as soon as the
    /// RPC process reports its port — its Kestrel listener is up, but IKVM/Mihon is
    /// still initializing (JCEF load + repository refresh can take several seconds).
    /// Calling into the bridge during that window fails with
    /// "RepositoryManager is not initialized".
    ///
    /// This gate polls ControlService.Ping until the RPC side reports Ready, then
    /// completes. It is reset whenever the supervisor restarts the process.
    /// </summary>
    private volatile TaskCompletionSource _bridgeReady = NewBridgeTcs();
    private int _bridgeReadyPort;

    private static TaskCompletionSource NewBridgeTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RpcBridgeClient(IRpcReadinessGate gate, IRpcProcessSupervisor supervisor, ILogger<RpcBridgeClient> logger)
    {
        _gate = gate;
        _supervisor = supervisor;
        _logger = logger;

        // When the supervisor restarts RPC, its listening gate reopens on a NEW
        // channel/port; the bridge-readiness gate must be reset so callers wait for
        // the fresh process instead of observing the previous one's readiness.
        _supervisor.ProcessRestarted += OnProcessRestarted;
        _supervisor.ProcessStopped += OnProcessRestarted;
    }

    /// <summary>True once RPC has completed its handshake at least once.</summary>
    public bool Initialized => _gate.Port > 0;

    /// <summary>
    /// Completes when the bridge inside RPC is actually usable (IKVM/Mihon
    /// initialized), not merely when the RPC process is listening.
    /// </summary>
    public Task InitializationCompleted => BridgeReadyAsync(CancellationToken.None);

    public IExtensionManager LocalExtensionManager => this;

    public IRepositoryManager OnlineRepositoryManager => this;

    /// <summary>
    /// Waits for the RPC process to be listening AND for the bridge inside it to
    /// report ready. The backend no longer initializes the bridge in-process.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => BridgeReadyAsync(cancellationToken);

    private void OnProcessRestarted()
    {
        _logger.LogInformation("RPC process restarted; resetting bridge-readiness gate.");
        Interlocked.Exchange(ref _bridgeReady, NewBridgeTcs());
        Volatile.Write(ref _bridgeReadyPort, 0);
    }

    /// <summary>
    /// Resolves once the RPC bridge is ready, starting the poll loop on first use.
    /// Fast-paths when already known-ready; a restart resets the gate so the wait
    /// naturally re-applies.
    /// </summary>
    private async Task BridgeReadyAsync(CancellationToken cancellationToken)
    {
        await _gate.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task current = _bridgeReady.Task;
        if (current.IsCompleted)
            return;

        await EnsureBridgeReadinessPollingAsync(cancellationToken).ConfigureAwait(false);
        await _bridgeReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts (idempotently, per port) a background poll of ControlService.Ping until
    /// the RPC side reports Ready. Keyed by port so a restart launches a fresh poll.
    /// </summary>
    private async Task EnsureBridgeReadinessPollingAsync(CancellationToken cancellationToken)
    {
        int port = _gate.Port;
        if (port <= 0)
            return;

        if (Volatile.Read(ref _bridgeReadyPort) == port)
            return; // poll already running for this incarnation

        Volatile.Write(ref _bridgeReadyPort, port);
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromMilliseconds(150);
            var maxDelay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    await _gate.Ready.ConfigureAwait(false);
                    if (_gate.Port != port)
                        return; // process was replaced; a new poll owns this

                    // RAW channel: the readiness poll must NOT go through the
                    // bridge-ready gate (that would deadlock — it would wait for the very
                    // readiness it is meant to establish).
                    var channel = await GetChannelRawAsync(CancellationToken.None).ConfigureAwait(false);
                    var client = new ControlService.ControlServiceClient(channel);
                    PingReply reply = await client.PingAsync(
                        new PingRequest { Token = _gate.Token }, _gate.BuildAuthMetadata())
                        .ConfigureAwait(false);

                    if (reply.Ready)
                    {
                        _bridgeReady.TrySetResult();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Not fatal: RPC may still be bringing the listener up.
                    _logger.LogDebug(ex, "Bridge readiness probe failed; retrying.");
                }

                await Task.Delay(delay).ConfigureAwait(false);
                if (delay < maxDelay)
                    delay += TimeSpan.FromMilliseconds(150);
            }
        });
    }

    /// <summary>
    /// RPC shutdown is owned by the supervisor (graceful PrepareShutdown), so
    /// this is intentionally a no-op here.
    /// </summary>
    public void Shutdown()
    {
    }

    /// <summary>
    /// Reads the bridge-global preferences the RPC host currently holds. The backend is the
    /// source of truth for settings (its SettingsService owns them), so this is mainly a
    /// diagnostic/reconciliation read; callers that just want the effective settings should use
    /// their own persisted copy.
    /// </summary>
    public async Task<Preferences> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        var (channel, headers) = await GetCallContextAsync(cancellationToken).ConfigureAwait(false);
        var client = new ControlService.ControlServiceClient(channel);
        JsonResult result = await client.GetPreferencesAsync(
            new BridgePreferencesRequest { Token = _gate.Token }, headers, null, cancellationToken)
            .ConfigureAwait(false);
        return RpcJson.Deserialize<Preferences>(result.Json) ?? new Preferences();
    }

    /// <summary>
    /// Pushes the backend's bridge-global preferences (FlareSolverr, SocksProxy, Browser,
    /// interceptors) into the RUNNING RPC host, replacing the old in-process live push that the
    /// RPC split removed. Without this the host only ever saw whatever <c>preferences.json</c> it
    /// loaded at startup, so a FlareSolverr toggle (or a missing file) left Cloudflare challenges
    /// unsolvable.
    /// </summary>
    public async Task SetPreferencesAsync(Preferences prefs, CancellationToken cancellationToken)
    {
        var (channel, headers) = await GetCallContextAsync(cancellationToken).ConfigureAwait(false);
        var client = new ControlService.ControlServiceClient(channel);
        await client.SetPreferencesAsync(
            new SetBridgePreferencesRequest { Token = _gate.Token, PreferencesJson = RpcJson.Serialize(prefs) },
            headers, null, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Pushed bridge preferences to RPC (FlareSolverr enabled={Enabled}, url={Url}).",
            prefs.FlareSolverr?.Enabled, prefs.FlareSolverr?.Url);
    }

    // ---------------------------------------------------------------------
    // ExtensionsService-backed surface
    // ---------------------------------------------------------------------

    public Task<List<RepositoryGroup>> ListExtensionsAsync(CancellationToken token = default)
        => ListExtensionsAsync(includeTemporary: false, token);

    public Task<List<RepositoryGroup>> ListExtensionsAsync(bool includeTemporary, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            JsonResult result = await client.ListInstalledExtensionsAsync(
                new ListInstalledRequest { IncludeTemporary = includeTemporary }, headers, null, token)
                .ConfigureAwait(false);
            return RpcJson.Deserialize<List<RepositoryGroup>>(result.Json) ?? [];
        });

    public List<RepositoryGroup> ListExtensions()
        => ListExtensions(includeTemporary: false);

    public List<RepositoryGroup> ListExtensions(bool includeTemporary)
        => ListExtensionsAsync(includeTemporary).GetAwaiter().GetResult();

    public RepositoryGroup? FindExtension(string name, bool includeTemporary = false)
        => ListExtensions(includeTemporary).FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<IExtensionInterop> GetInteropAsync(RepositoryGroup entry, CancellationToken token = default)
    {
        await _gate.Ready.WaitAsync(token).ConfigureAwait(false);
        RepositoryEntry? activeEntry = entry.Entries.ElementAtOrDefault(entry.ActiveEntry)
                                      ?? entry.Entries.FirstOrDefault();
        string? package = activeEntry?.Extension?.Package;
        if (string.IsNullOrWhiteSpace(package))
            throw new InvalidOperationException($"Extension group '{entry.Name}' has no resolvable package.");

        // Sources are resolved lazily by the RPC side per call; expose descriptors
        // immediately so callers can enumerate sources without an extra round trip.
        List<ISourceInterop> sources = await ListSourcesAsync(package, token).ConfigureAwait(false);

        // Carry the active version so the backend's stale-guard can tell a pinned
        // interop apart from a genuinely version-changed one. Without it the proxy
        // reports an empty version and the guard evicts on every access.
        string version = activeEntry?.Extension?.Version ?? string.Empty;
        return new RpcExtensionInterop(entry.Name, package, sources, version);
    }

    /// <summary>
    /// Fetches source descriptors for a package from RPC and materializes them
    /// as <see cref="RpcSourceInterop"/> proxies.
    /// </summary>
    public Task<List<SourceDescriptor>> ListSourceDescriptorsAsync(string package, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            JsonResult result = await client.ListSourcesAsync(
                new PackageRequest { PackageId = package }, headers, null, token).ConfigureAwait(false);
            return RpcJson.Deserialize<List<SourceDescriptor>>(result.Json) ?? [];
        });

    /// <summary>
    /// Prepares an online extension for a sweep (download/compile + register as temporary) and
    /// returns its descriptor, or null when the package is not available online.
    /// </summary>
    public Task<OnlineExtensionDescriptor?> PrepareOnlineExtensionAsync(string package, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            JsonResult result = await client.PrepareOnlineExtensionAsync(
                new PackageRequest { PackageId = package }, headers, null, token).ConfigureAwait(false);
            return RpcJson.Deserialize<OnlineExtensionDescriptor>(result.Json);
        });

    /// <summary>Releases a temporary extension previously prepared for a sweep.</summary>
    public Task<bool> ReleaseOnlineExtensionAsync(string package, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            BoolValue ok = await client.ReleaseOnlineExtensionAsync(
                new PackageRequest { PackageId = package }, headers, null, token).ConfigureAwait(false);
            return ok.Value;
        });

    /// <summary>Releases every temporary extension prepared for a sweep.</summary>
    public async Task<int> ReleaseAllTemporaryExtensionsAsync(CancellationToken token = default)
    {
        // The RPC host owns the temporary registry; enumerate what it currently holds and release
        // each prepared package. Best-effort: a failure on one package must not abort the rest.
        List<RepositoryGroup> groups = await ListExtensionsAsync(includeTemporary: true, token).ConfigureAwait(false);
        int released = 0;
        foreach (RepositoryGroup group in groups.Where(g => g.Temporary))
        {
            string? package = (group.Entries.ElementAtOrDefault(group.ActiveEntry) ?? group.Entries.FirstOrDefault())
                ?.Extension?.Package;
            if (string.IsNullOrWhiteSpace(package))
                continue;
            try
            {
                if (await ReleaseOnlineExtensionAsync(package, token).ConfigureAwait(false))
                    released++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release temporary extension {Package}.", package);
            }
        }
        return released;
    }

    public async Task<ISourceInterop> GetSourceInteropAsync(string package, long sourceId, CancellationToken token = default)
    {
        List<SourceDescriptor> descriptors = await ListSourceDescriptorsAsync(package, token).ConfigureAwait(false);
        SourceDescriptor descriptor = descriptors.FirstOrDefault(d => d.SourceId == sourceId)
            ?? throw new InvalidOperationException($"Source {sourceId} not found in {package}.");
        descriptor.PackageId = package;
        return new RpcSourceInterop(this, descriptor);
    }

    private async Task<List<ISourceInterop>> ListSourcesAsync(string package, CancellationToken token)
    {
        List<SourceDescriptor> descriptors = await ListSourceDescriptorsAsync(package, token).ConfigureAwait(false);
        return descriptors
            .Select(d =>
            {
                d.PackageId = package;
                return (ISourceInterop)new RpcSourceInterop(this, d);
            })
            .ToList();
    }

    public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null); // repository installs removed by design

    public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiRepository repository, TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null); // repository installs removed by design

    public Task<RepositoryGroup?> AddExtensionAsync(byte[] apk, bool force = false, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            using var call = client.InstallCustomApk(headers, null, token);
            await call.RequestStream.WriteAsync(new InstallApkRequest
            {
                Force = force,
                Data = Google.Protobuf.ByteString.CopyFrom(apk),
                Last = true,
            }).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            JsonResult result = await call.ResponseAsync.ConfigureAwait(false);
            return RpcJson.Deserialize<RepositoryGroup>(result.Json);
        });

    public Task<bool> RemoveExtensionAsync(RepositoryGroup group, CancellationToken token = default)
        => Task.FromResult(false); // uninstall removed by design; disable is local state only

    public Task<RepositoryGroup?> RemoveExtensionVersionAsync(RepositoryEntry entry, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            string package = entry.Extension?.Package ?? entry.Name;
            BoolValue ok = await client.RemoveVersionAsync(
                new SetActiveVersionRequest { PackageId = package, Version = entry.Extension?.Version ?? string.Empty },
                headers, null, token).ConfigureAwait(false);

            return ok.Value
                ? new RepositoryGroup { Name = package, Entries = [entry], ActiveEntry = 0 }
                : null;
        });

    public Task<RepositoryGroup> SetActiveExtensionVersionAsync(RepositoryGroup group, CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            RepositoryEntry active = group.Entries.ElementAtOrDefault(group.ActiveEntry) ?? group.Entries[0];
            await client.SetActiveVersionAsync(new SetActiveVersionRequest
            {
                PackageId = active.Extension?.Package ?? group.Name,
                Version = active.Extension?.Version ?? string.Empty,
                AutoUpdate = group.AutoUpdate,
            }, headers, null, token).ConfigureAwait(false);

            return group;
        });

    // ---------------------------------------------------------------------
    // IRepositoryManager
    // ---------------------------------------------------------------------

    public Task<TachiyomiRepository> AddOnlineRepositoryAsync(TachiyomiRepository repository, CancellationToken token = default)
        => Task.FromResult(repository); // repository config is RPC-owned; nothing to add here

    public List<TachiyomiRepository> ListOnlineRepositories()
        => ListOnlineRepositoriesAsync().GetAwaiter().GetResult();

    public async Task<List<TachiyomiRepository>> ListOnlineRepositoriesAsync(CancellationToken token = default)
        => await CallAsync(async (client, headers) =>
        {
            JsonResult result = await client.ListAvailableExtensionsAsync(
                new ListAvailableRequest { Refresh = false }, headers, null, token).ConfigureAwait(false);
            return RpcJson.Deserialize<List<TachiyomiRepository>>(result.Json) ?? [];
        }).ConfigureAwait(false);

    public Task RefreshAllRepositoriesAsync(CancellationToken token = default)
        => CallAsync(async (client, headers) =>
        {
            await client.ListAvailableExtensionsAsync(
                new ListAvailableRequest { Refresh = true }, headers, null, token).ConfigureAwait(false);
            return true;
        });

    public Task<bool> RemoveOnlineRespositoryAsync(TachiyomiRepository repository, CancellationToken token = default)
        => Task.FromResult(false); // repository config is RPC-owned

    /// <summary>
    /// Streams an extension icon on demand from RPC. The backend does NOT read the
    /// extension folder itself (RPC owns those files), so icons always come over the
    /// wire. Returns null when the icon is missing.
    /// </summary>
    public async Task<Stream?> OpenExtensionIconAsync(string packageId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        var (channel, headers) = await GetCallContextAsync(token).ConfigureAwait(false);
        var client = new ExtensionsService.ExtensionsServiceClient(channel);

        try
        {
            using var call = client.GetExtensionIcon(
                new PackageRequest { PackageId = packageId }, headers, null, token);

            var buffer = new MemoryStream();
            await foreach (Chunk chunk in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (chunk.Data.Length > 0)
                    buffer.Write(chunk.Data.Span);
            }

            if (buffer.Length == 0)
                return null;

            buffer.Position = 0;
            return buffer;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // No icon for this package — not an error.
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Channel/dispatch plumbing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Gated channel getter used by every real operation: blocks until the process is
    /// LISTENING *and* the bridge inside it is READY (IKVM/Mihon initialized), so calls
    /// issued during a restart or the init window wait instead of failing.
    /// </summary>
    private async Task<GrpcChannel> GetChannelAsync(CancellationToken token)
    {
        if (!_bridgeReady.Task.IsCompleted)
        {
            _logger.LogDebug("Operation waiting for RPC bridge readiness (listening={Port})...", _gate.Port);
            // Diagnostic: if this takes long, the backend is blocked on readiness — not the child —
            // (distinguishes "backend stuck" from "child dead").
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                    if (!_bridgeReady.Task.IsCompleted)
                        _logger.LogWarning(
                            "RPC bridge readiness still not confirmed after 20s (listening={Port}). Backend calls are blocked.",
                            _gate.Port);
                }
                catch { /* ignore */ }
            });
        }

        await BridgeReadyAsync(token).ConfigureAwait(false);
        return await GetChannelRawAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw channel getter (listening-only). Used by the readiness poll and internally; it
    /// deliberately does NOT await the bridge-ready gate to avoid a self-deadlock.
    /// </summary>
    private async Task<GrpcChannel> GetChannelRawAsync(CancellationToken token)
    {
        await _gate.Ready.WaitAsync(token).ConfigureAwait(false);

        int port = _gate.Port;
        if (_channel != null && _channelPort == port)
            return _channel;

        await _channelLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_channel != null && _channelPort == port)
                return _channel;

            _channel?.Dispose();
            _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
            {
                // Large JSON catalogs/chapter lists exceed gRPC's 4 MiB default; raise both
                // the receive and send ceilings so neither the backend nor the host rejects
                // a payload the other can legitimately produce (ResourceExhausted fixes).
                MaxReceiveMessageSize = RpcGrpcSizes.MaxMessageSize,
                MaxSendMessageSize = RpcGrpcSizes.MaxMessageSize,
            });
            _channelPort = port;
            _logger.LogInformation("RPC channel bound to 127.0.0.1:{Port}.", port);
            return _channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    /// <summary>
    /// Waits readiness, opens the channel, and runs the supplied call with the
    /// per-start token header attached.
    /// </summary>
    private async Task<T> CallAsync<T>(Func<ExtensionsService.ExtensionsServiceClient, Grpc.Core.Metadata, Task<T>> action)
    {
        var headers = _gate.BuildAuthMetadata();
        GrpcChannel channel = await GetChannelAsync(CancellationToken.None).ConfigureAwait(false);
        var client = new ExtensionsService.ExtensionsServiceClient(channel);
        return await action(client, headers).ConfigureAwait(false);
    }

    /// <summary>
    /// Exposes the resolved channel + headers + readiness wait to the source
    /// proxy so catalog/content calls share the same plumbing.
    /// </summary>
    internal async Task<(GrpcChannel Channel, Grpc.Core.Metadata Headers)> GetCallContextAsync(CancellationToken token)
    {
        GrpcChannel channel = await GetChannelAsync(token).ConfigureAwait(false);
        return (channel, _gate.BuildAuthMetadata());
    }

    public void Dispose()
    {
        try { _channel?.Dispose(); } catch { /* best effort */ }
        _channelLock.Dispose();
    }
}
