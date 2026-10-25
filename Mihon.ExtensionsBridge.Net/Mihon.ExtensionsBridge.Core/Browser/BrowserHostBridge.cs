using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Microsoft.Extensions.Logging;

namespace Mihon.ExtensionsBridge.Core.Browser
{
    /// <summary>
    /// C#-side facade that installs an <see cref="IBrowserAutomationService"/> implementation into
    /// the Kotlin <c>extension.bridge.browser.BrowserHostGateway</c> slot (plan §4.3).
    ///
    /// The direction is inverted relative to <c>CefPumpBridge</c>: the contract is declared in
    /// Kotlin and the C# host installs an implementation into a settable slot. Only a single JSON
    /// hop (<see cref="BrowserRequest"/> <-> <see cref="BrowserResponse"/>) crosses into IKVM.
    ///
    /// Installation is done through <c>java.lang.reflect.Proxy</c> so this assembly does not need a
    /// compile-time reference to the (separately rebuilt) Android.Compat.dll. If the gateway class
    /// is absent (older jar) or installation fails, the whole operation is a logged no-op and the
    /// Java layer keeps its existing direct-network/FlareSolverr degradation.
    /// </summary>
    public static class BrowserHostBridge
    {
        private const string GatewayClassName = "extension.bridge.browser.BrowserHostGateway";
        private const string InterfaceClassName = "extension.bridge.browser.IBrowserHost";

        /// <summary>
        /// How long a WebView will wait for a browser-session slot before giving up. Generous: the
        /// correct behaviour is to queue behind in-flight sessions, and this bound exists only so a
        /// genuine stall is reported rather than hanging forever.
        /// </summary>
        /// <summary>
        /// How long a WebView will wait for a browser-session slot before giving up.
        ///
        /// Deliberately SHORT. Extensions rarely call <c>WebView.destroy()</c>, so pooled sessions
        /// (which still hold a slot) accumulate until the cap is reached; a long wait then turns
        /// into an unbounded stall in which every new WebView parks a thread. Observed symptom:
        /// logging stopped entirely across BOTH processes. A short bound turns that into a logged,
        /// recoverable failure instead of a silent hang.
        /// </summary>
        private static readonly TimeSpan SessionSlotWait = TimeSpan.FromSeconds(60);

        /// <summary>Granularity of the wait-for-a-slot poll, so idle sessions are re-checked often.</summary>
        private static readonly TimeSpan SlotWaitSlice = TimeSpan.FromMilliseconds(250);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private static volatile bool _installed;

        public static bool IsInstalled => _installed;

        /// <summary>
        /// Installs <paramref name="service"/> into the Kotlin gateway. Returns true when the host
        /// was successfully installed.
        /// </summary>
        public static bool TryInstall(IBrowserAutomationService service, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(logger);

            if (_installed)
            {
                return true;
            }

            try
            {
                var gatewayClass = java.lang.Class.forName(GatewayClassName);
                var interfaceClass = java.lang.Class.forName(InterfaceClassName);

                var instance = gatewayClass.getField("INSTANCE").get(null);
                if (instance is null)
                {
                    logger.LogWarning("BrowserHostGateway.INSTANCE is null; browser host not installed.");
                    return false;
                }

                var handler = new BrowserHostInvocationHandler(service, logger);

                // Let service shutdown tear down pooled live-DOM sessions (they hold slots).
                if (service is BrowserAutomationService concreteService)
                {
                    concreteService.LiveDomSessionsDisposer = handler.DisposeIdleSessions;
                }
                var proxy = java.lang.reflect.Proxy.newProxyInstance(
                    interfaceClass.getClassLoader(),
                    new java.lang.Class[] { interfaceClass },
                    handler);

                var objectClass = java.lang.Class.forName("java.lang.Object");
                var setHost = gatewayClass.getMethod("setHost", objectClass);
                setHost.invoke(instance, new object?[] { proxy });

                // `setHost` returns Unit and can silently decline (a rejected cast, or the candidate
                // failing the interface test). Verify the slot actually took, instead of assuming —
                // otherwise C# reports success while the JVM keeps its null host.
                var isInstalled = gatewayClass.getMethod("isInstalled");
                // Kotlin's `isInstalled()` returns java.lang.Boolean. Testing `is bool` alone read
                // as false and produced a spurious "installation did NOT take effect" error while
                // the slot was in fact populated — so accept BOTH representations rather than
                // assuming which one this IKVM build hands back.
                var rawInstalled = isInstalled.invoke(instance);
                var installed = rawInstalled is bool netFlag
                    ? netFlag
                    : java.lang.Boolean.TRUE.equals(rawInstalled);
                if (!installed)
                {
                    logger.LogError(
                        "Browser host installation did NOT take effect: {Gateway}.isInstalled() is false " +
                        "right after setHost(). The JVM will keep degrading to the direct network chain.",
                        GatewayClassName);
                    return false;
                }

                _installed = true;
                logger.LogInformation("Browser host installed into the AndroidCompat browser gateway (verified).");
                return true;
            }
            catch (Exception ex)
            {
                // Includes java.lang.ClassNotFoundException when the jar predates the gateway.
                logger.LogDebug(ex, "Browser host installation skipped (gateway unavailable).");
                return false;
            }
        }

        /// <summary>
        /// <c>java.lang.reflect.InvocationHandler</c> bridging the Kotlin <c>IBrowserHost</c>
        /// methods to the .NET <see cref="IBrowserAutomationService"/>.
        /// </summary>
        private sealed class BrowserHostInvocationHandler : java.lang.reflect.InvocationHandler
        {
            private readonly IBrowserAutomationService _service;
            private readonly ILogger _logger;

            /// <summary>Live-DOM sessions keyed by handle (Tier 2).</summary>
            private readonly ConcurrentDictionary<long, IBrowserSession> _sessions = new();

            /// <summary>
            /// Handles of pooled-but-idle sessions available for reuse (the port of
            /// <c>WebViewPool</c>'s idle-entry scan). A reused session needs no new browser and no
            /// new slot.
            /// </summary>
            private readonly ConcurrentStack<long> _idleHandles = new();

            /// <summary>Whether each session handle is currently leased out to a WebView.</summary>
            private readonly ConcurrentDictionary<long, bool> _inUse = new();

            /// <summary>
            /// Cookies seeded from the JVM store, keyed by host. The JVM store remains the source
            /// of truth (plan §12.2); Tier-1 fetches re-seed from here so a cleared challenge is
            /// reused, and cookies observed during a fetch are written back by the caller.
            /// </summary>
            private readonly ConcurrentDictionary<string, List<BrowserCookie>> _seededCookies =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Last URL navigated per session handle, so <see cref="AwaitLoad"/> can harvest the
            /// session's cookies for the right origin once the page has settled.
            /// </summary>
            private readonly ConcurrentDictionary<long, string> _lastUrl = new();

            /// <summary>JVM objects exposed to the page, keyed by (handle, interfaceName).</summary>
            private readonly ConcurrentDictionary<(long Handle, string Name), object?> _targets = new();

            /// <summary>Sessions that already have the single `__rensaio` dispatch binding.</summary>
            private readonly ConcurrentDictionary<long, bool> _bindingSessions = new();

            private long _nextHandle;

            public BrowserHostInvocationHandler(IBrowserAutomationService service, ILogger logger)
            {
                _service = service;
                _logger = logger;
            }

            public object? invoke(object? proxy, java.lang.reflect.Method? method, object?[]? args)
            {
                var name = method?.getName() ?? "";

                try
                {
                    switch (name)
                    {
                        case "execute":
                            return Execute(AsString(args, 0));

                        case "getCookie":
                            return GetCookie(AsString(args, 0));

                        case "setCookie":
                            SetCookie(AsString(args, 0), AsString(args, 1));
                            return null;

                        // NOTE: every non-void return MUST be the BOXED Java type. IKVM's proxy
                        // casts the handler's result to the method's declared return type, so
                        // returning a .NET primitive fails with e.g.
                        //   java.lang.ClassCastException: Unable to cast object of type
                        //   'System.Boolean' to type 'java.lang.Boolean'
                        // which previously made the whole provider look unavailable.
                        case "isAvailable":
                            return java.lang.Boolean.valueOf(_service.IsAvailable);

                        case "capacityAvailable":
                            return java.lang.Boolean.valueOf(CapacityAvailable());

                        // ── Tier 2 (live DOM) ───────────────────────────────
                        case "create":
                            return java.lang.Long.valueOf(CreateSession());

                        case "destroy":
                            DestroySession(AsLong(args, 0));
                            return null;

                        case "navigate":
                            return Navigate(AsLong(args, 0), AsString(args, 1));

                        case "awaitLoad":
                            AwaitLoad(AsLong(args, 0));
                            return null;

                        case "evaluate":
                            return Evaluate(AsLong(args, 0), AsString(args, 1));

                        case "addInitScript":
                            AddInitScript(AsLong(args, 0), AsString(args, 1));
                            return null;

                        case "setDocument":
                            SetDocument(AsLong(args, 0), AsString(args, 1), AsString(args, 2));
                            return null;

                        case "addBinding":
                            AddBinding(AsLong(args, 0), AsString(args, 1), args is { Length: > 2 } ? args[2] : null);
                            return null;

                        case "screenshot":
                            return Screenshot(AsLong(args, 0));

                        case "syncCookies":
                            SyncCookies(AsString(args, 0), AsString(args, 1));
                            return null;

                        case "toString":
                            return "BrowserHostAdapter";

                        case "hashCode":
                            return java.lang.Integer.valueOf(GetHashCode());

                        case "equals":
                            return java.lang.Boolean.valueOf(
                                ReferenceEquals(proxy, args is { Length: > 0 } ? args[0] : null));

                        default:
                            _logger.LogDebug("Unhandled IBrowserHost method: {Method}", name);
                            return null;
                    }
                }
                catch (Exception ex)
                {
                    // Never let an exception cross the IKVM boundary: surface it as a JSON error
                    // response so the interceptor can degrade to the direct network chain.
                    _logger.LogWarning(ex, "IBrowserHost.{Method} failed.", name);
                    if (name == "execute")
                    {
                        return JsonSerializer.Serialize(
                            new BrowserResponse { Error = ex.Message },
                            JsonOptions);
                    }
                    // Every non-reference return must be boxed here too, otherwise the proxy's
                    // cast fails on the error path and turns a handled failure into an
                    // unhandled ClassCastException across the boundary.
                    return name switch
                    {
                        "isAvailable" or "capacityAvailable" => java.lang.Boolean.FALSE,
                        "equals" => java.lang.Boolean.FALSE,
                        // A failed session create still has to be a legal Java `long`.
                        "create" => java.lang.Long.valueOf(0L),
                        "hashCode" => java.lang.Integer.valueOf(0),
                        // String-returning methods (string, getCookie, navigate, evaluate) and
                        // void methods legitimately return null: null is valid for a Java
                        // reference type.
                        _ => null,
                    };
                }
            }

            private string Execute(string requestJson)
            {
                var request = string.IsNullOrWhiteSpace(requestJson)
                    ? new BrowserRequest()
                    : JsonSerializer.Deserialize<BrowserRequest>(requestJson, JsonOptions) ?? new BrowserRequest();

                // Merge the caller-supplied cookies with anything seeded earlier for this host, so
                // the in-page fetch carries the same cookies the JVM chain would.
                var host = ResolveHost(request.Url);
                var merged = MergeCookies(host, request.Cookies);
                if (merged.Count > 0)
                {
                    request = CloneWithCookies(request, merged);
                }

                var response = _service
                    .FetchAsync(request, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                // Remember what the browser observed so later fetches reuse the session state.
                RememberCookies(host, response.Cookies);

                return JsonSerializer.Serialize(response, JsonOptions);
            }

            private static string ResolveHost(string url) =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "default";

            /// <summary>Merges incoming cookies over the seeded set (last write wins per name).</summary>
            private IReadOnlyList<BrowserCookie> MergeCookies(string host, IReadOnlyList<BrowserCookie> incoming)
            {
                var merged = new Dictionary<string, BrowserCookie>(StringComparer.Ordinal);

                if (_seededCookies.TryGetValue(host, out var seeded))
                {
                    lock (seeded)
                    {
                        foreach (var cookie in seeded)
                        {
                            merged[cookie.Name] = cookie;
                        }
                    }
                }

                foreach (var cookie in incoming)
                {
                    merged[cookie.Name] = cookie;
                }

                return merged.Values.ToArray();
            }

            /// <summary>Records cookies observed by the browser for reuse on later fetches.</summary>
            private void RememberCookies(string host, IReadOnlyList<BrowserCookie> observed)
            {
                if (observed.Count == 0)
                {
                    return;
                }

                var bucket = _seededCookies.GetOrAdd(host, _ => []);
                lock (bucket)
                {
                    foreach (var cookie in observed)
                    {
                        var existing = bucket.FindIndex(c => string.Equals(c.Name, cookie.Name, StringComparison.Ordinal));
                        if (existing >= 0)
                        {
                            bucket[existing] = cookie;
                        }
                        else
                        {
                            bucket.Add(cookie);
                        }
                    }
                }
            }

            private static BrowserRequest CloneWithCookies(BrowserRequest source, IReadOnlyList<BrowserCookie> cookies) => new()
            {
                Url = source.Url,
                Method = source.Method,
                Headers = source.Headers,
                Body = source.Body,
                ContextUrl = source.ContextUrl,
                TimeoutSeconds = source.TimeoutSeconds,
                WaitUntil = source.WaitUntil,
                Cookies = cookies,
            };

            private string GetCookie(string url)
            {
                // Cookie round-trip through a short-lived session; keeps the surface minimal.
                var session = _service.OpenSessionAsync(null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                try
                {
                    var cookies = session
                        .GetCookiesAsync(url, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    return JsonSerializer.Serialize(cookies, JsonOptions);
                }
                finally
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            private void SetCookie(string url, string value)
            {
                var cookies = JsonSerializer.Deserialize<BrowserCookie[]>(value, JsonOptions) ?? [];

                // Record ONLY in the per-host jar. The jar is the single seeding source for BOTH
                // Tier-1 fetches (MergeCookies) and live-DOM sessions (SeedSessionCookies), so no
                // browser session is opened here. Previously this opened a short-lived session per
                // call, which was wasteful and made this unusable on the hot path (e.g. pushing the
                // JVM store before every live-DOM navigation). Apply-on-next-use keeps it cheap.
                RememberCookies(ResolveHost(url), cookies);
            }

            private bool CapacityAvailable()
            {
                if (_service is BrowserAutomationService concrete)
                {
                    return concrete.CapacityAvailable;
                }
                return _service.IsAvailable;
            }

            // ── Tier 2 (live DOM) ───────────────────────────────────────────

            private long CreateSession()
            {
                // An idle pooled session still HOLDS its slot, so waiting on the semaphore alone
                // would starve every new WebView behind sessions that are reusable but unclaimed.
                // Poll in slices: between waits, re-check the idle stack so a returned session is
                // taken immediately. (This is the WebViewPool.acquire() reuse path, made usable
                // while contended.)
                var deadline = DateTime.UtcNow + SessionSlotWait;

                while (true)
                {
                    while (_idleHandles.TryPop(out var idle))
                    {
                        if (!_sessions.TryGetValue(idle, out var candidate))
                        {
                            continue;
                        }

                        // HEALTH CHECK on reuse: a context a previous WebView left mid-navigation
                        // (or with a replaced document) would otherwise wedge the next runWebView —
                        // the "works, then hangs, then a fresh page succeeds" pattern. Probe it and
                        // discard a wedged session rather than hand it to the next WebView.
                        if (IsSessionAlive(candidate))
                        {
                            _inUse[idle] = true;
                            _logger.LogDebug(
                                "Reused idle browser session handle={Handle} ({Stats})",
                                idle,
                                _service.DescribeStats());
                            return idle;
                        }

                        _logger.LogWarning(
                            "Idle browser session handle={Handle} failed its liveness probe; discarding and opening a fresh session.",
                            idle);
                        DisposeSession(idle);
                    }

                    if (_service.TryReserveSessionSlot(SlotWaitSlice))
                    {
                        try
                        {
                            var session = _service
                                .OpenSessionAsync(null, CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();

                            var handle = Interlocked.Increment(ref _nextHandle);
                            _sessions[handle] = session;
                            _inUse[handle] = true;
                            // Lease accounting: session count vs the concurrency cap. If 'live' reaches
                            // 'max', further WebViews BLOCK in TryReserveSessionSlot — which is the shape of
                            // "the job never returns". Available at Debug (RENSAIO_RPC_VERBOSE=1).
                            _logger.LogDebug(
                                "Session leased handle={Handle} ({Stats})",
                                handle,
                                _service.DescribeStats());
                            return handle;
                        }
                        catch (Exception ex)
                        {
                            // Never hold a reserved slot for a session that was not created.
                            _service.ReleaseSessionSlot();
                            _logger.LogWarning(ex, "Failed to open a browser session; slot released.");
                            return 0L;
                        }
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        // A real stall: every slot is held by a session that is neither released nor
                        // returned to the pool. Logged loudly and returned promptly — an unbounded
                        // wait here parks a thread per WebView and stalls the whole process.
                        _logger.LogError(
                            "Timed out after {Timeout}s waiting for a browser session slot ({Stats}). " +
                            "Slots are held by sessions that were neither released nor pooled — " +
                            "most likely WebViews the extension never destroys.",
                            SessionSlotWait.TotalSeconds,
                            _service.DescribeStats());
                        return 0L;
                    }
                }
            }

            /// Dispatches `__rensaio(interfaceName, method, argsJson)` to the registered JVM object
            /// via reflection (C#→Java). Returns a JSON-encoded result or "null".
            /// </summary>
            /// Returns the session to the idle pool instead of destroying it, mirroring
            /// <c>WebViewPool.release()</c>: the browser context (and its warm cookies/profile)
            /// stays alive for the next WebView that needs one. The slot is deliberately NOT
            /// released — a pooled session is still a live browser and must keep counting against
            /// the cap. Real disposal happens in <see cref="DisposeIdleSessions"/>.
            /// </summary>
            private void DestroySession(long handle)
            {
                if (handle == 0L || !_sessions.ContainsKey(handle))
                {
                    // Unknown/zero handle: nothing to pool, so nothing to release either.
                    return;
                }

                _inUse[handle] = false;
                _idleHandles.Push(handle);
                _logger.LogDebug("Returned browser session handle={Handle} to the idle pool.", handle);
            }

            /// <summary>
            /// Destroys every pooled session and releases its slot. Called when the whole bridge
            /// shuts down; a long-lived host otherwise keeps up to the cap in contexts forever.
            /// </summary>
            public void DisposeIdleSessions()
            {
                while (_idleHandles.TryPop(out var handle))
                {
                    DisposeSession(handle);
                }

                // Sessions still in use are also ours to tear down at shutdown.
                foreach (var handle in _sessions.Keys.ToArray())
                {
                    DisposeSession(handle);
                }
            }

            /// <summary>
            /// Cheap liveness probe for a reused live-DOM session: a one-word evaluate must return
            /// within a short bound. A session that is wedged (typically mid-navigation) is reported
            /// dead so the caller discards it instead of reusing it.
            /// </summary>
            private bool IsSessionAlive(IBrowserSession session)
            {
                try
                {
                    var probe = session.EvaluateAsync("1", CancellationToken.None);
                    if (!probe.Wait(TimeSpan.FromSeconds(5)))
                    {
                        return false;
                    }

                    return string.Equals(probe.Result, "1", StringComparison.Ordinal);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Browser session liveness probe failed.");
                    return false;
                }
            }

            private void DisposeSession(long handle)
            {
                _inUse.TryRemove(handle, out _);
                if (!_sessions.TryRemove(handle, out var session))
                {
                    return;
                }

                try
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing browser session handle={Handle}", handle);
                }
                finally
                {
                    _service.ReleaseSessionSlot();
                }
            }

            private string Navigate(long handle, string url)
            {
                // Returns at navigation COMMIT (see IBrowserSession.NavigateAsync): the Kotlin side
                // fires onPageStarted immediately after, then calls awaitLoad.
                var session = ResolveSession(handle);
                SeedSessionCookies(session, url);
                _lastUrl[handle] = url;
                session.NavigateAsync(url, CancellationToken.None).GetAwaiter().GetResult();
                return url;
            }

            /// <summary>
            /// Seeds the live-DOM session's browser context with the cookies the JVM store has for
            /// this host — most importantly <c>cf_clearance</c> solved by the CloudflareInterceptor.
            ///
            /// Without this the live-DOM context has its OWN cookie jar (a fresh persistent profile),
            /// so it is NOT recognised by the WAF even though the JVM chain solved the challenge: the
            /// served page then loads Cloudflare's <c>/cdn-cgi/challenge-platform/scripts/jsd/main.js</c>
            /// and never issues its own /api/ request, so the extension times out. This is exactly the
            /// intermittent failure seen on comix (pages 1-2 fine, page 3 challenged).
            /// </summary>
            private void SeedSessionCookies(IBrowserSession session, string url)
            {
                var host = ResolveHost(url);
                if (!_seededCookies.TryGetValue(host, out var seeded) || seeded.Count == 0)
                {
                    return;
                }

                BrowserCookie[] snapshot;
                lock (seeded)
                {
                    snapshot = seeded.ToArray();
                }

                // Cloudflare cookies are fingerprint/IP-bound. Seeding a solver-obtained clearance
                // into a browser session that ALREADY holds its own (better, fingerprint-matched)
                // one would clobber it. So: always seed the non-CF cookies (session/auth/prefs flow
                // freely both ways), and seed CF cookies ONLY when the session has none yet.
                var nonCf = snapshot.Where(c => !IsCloudflareCookie(c.Name)).ToArray();
                var cf = snapshot.Where(c => IsCloudflareCookie(c.Name)).ToArray();

                var toSeed = nonCf;
               
                if (cf.Length > 0 && !SessionHasCloudflareCookie(session, url))
                {
                    toSeed = nonCf.Concat(cf).ToArray();
                }
              
                if (toSeed.Length == 0)
                {
                    return;
                }

                try
                {
                    session.SetCookiesAsync(url, toSeed, CancellationToken.None).GetAwaiter().GetResult();
                    _logger.LogInformation(
                        "Seeded live-DOM session with {Count} cookie(s) for {Host} (cfSeeded={CfSeeded}).",
                        toSeed.Length,
                        host,
                        toSeed.Length > nonCf.Length);
                }
                catch (Exception ex)
                {
                    // Non-fatal: a cookie that cannot be set (bad domain/expiry) must not fail the
                    // whole navigation — the page may still work.
                    _logger.LogDebug(ex, "Seeding live-DOM session cookies failed (ignored).");
                }
            }

            /// <summary>True when the session's own jar already holds a Cloudflare cookie for the origin.</summary>
            private bool SessionHasCloudflareCookie(IBrowserSession session, string url)
            {
                try
                {
                    var existing = session
                        .GetCookiesAsync(url, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    return existing.Any(c => IsCloudflareCookie(c.Name));
                }
                catch (Exception ex)
                {
                    // If we cannot tell, prefer NOT to clobber: treat as "has one".
                    _logger.LogDebug(ex, "CF-cookie presence probe failed; skipping CF seed.");
                    return true;
                }
            }

            /// <summary>
            /// Cloudflare-managed cookies. These are bound to the solving fingerprint/IP, so they are
            /// shared JVM -> browser only (and only when the browser has none) and are never harvested
            /// back — a browser-obtained clearance is useless to OkHttp's different fingerprint, and a
            /// solver-obtained one must not overwrite the browser's own.
            /// </summary>
            private static bool IsCloudflareCookie(string name) =>
                name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__cf_bm", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__cf_ob", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__cfwaitingroom", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("_cfuvid", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cf_use_ob", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cf_ob_info", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("cf_chl_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("__cf", StringComparison.OrdinalIgnoreCase);

            /// <summary>
            /// Blocks until the page started by the most recent navigate/setDocument reaches its
            /// `load` event. Paired with the commit-returning navigate/setDocument to reproduce the
            /// real WebView onPageStarted -> load -> onPageFinished ordering.
            /// </summary>
            private void AwaitLoad(long handle)
            {
                var session = ResolveSession(handle);
                session
                    .WaitForLoadAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                // Harvest AFTER the load settles: this is when Cloudflare may have set a fresh
                // cf_clearance during the live-DOM navigation. Storing it in the shared jar means
                // (a) it is seeded forward into every later session, and (b) Tier-1 fetches reuse it.
                // This closes the forwarding loop: a clearance obtained by the browser is no longer
                // lost when the session/profile goes away.
                if (_lastUrl.TryGetValue(handle, out var url))
                {
                    HarvestSessionCookies(session, url);
                }
            }

            /// <summary>
            /// Reads the cookies the live-DOM session currently holds for <paramref name="url"/> and
            /// merges them into the shared per-host jar, so a clearance the browser obtained is
            /// reused (seed-forward) rather than discarded. Best-effort and non-fatal.
            /// </summary>
            private void HarvestSessionCookies(IBrowserSession session, string url)
            {
                try
                {
                    var cookies = session
                        .GetCookiesAsync(url, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    // Exclude Cloudflare cookies: a browser-obtained clearance is bound to the
                    // browser's fingerprint and is not reusable by OkHttp (nor should it be seeded
                    // back over a solver cookie). Only fingerprint-independent cookies are shared
                    // back into the jar.
                    var shared = cookies.Where(c => !IsCloudflareCookie(c.Name)).ToArray();
                    if (shared.Length == 0)
                    {
                        return;
                    }

                    RememberCookies(ResolveHost(url), shared);
                    _logger.LogDebug(
                        "Harvested {Count} non-CF cookie(s) from live-DOM session for {Host}.",
                        shared.Length,
                        ResolveHost(url));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Harvesting live-DOM session cookies failed (ignored).");
                }
            }

            private string Evaluate(long handle, string script)
            {
                var session = ResolveSession(handle);
                return session.EvaluateAsync(script, CancellationToken.None).GetAwaiter().GetResult() ?? "";
            }

            /// <summary>
            /// Registers a document-start script. Used for the `onPageStarted` scripts that must
            /// install interceptor hooks BEFORE the page's own bundles run; evaluating them after
            /// the load is always too late (the page's API calls have already been made).
            /// </summary>
            private void AddInitScript(long handle, string script)
            {
                ResolveSession(handle)
                    .AddInitScriptAsync(script, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            /// <summary>
            /// Replaces the page content without navigating (the `loadDataWithBaseURL` case). The
            /// session re-applies the JS-interface init scripts as part of this, which is the whole
            /// point: `document.write` does not trigger init scripts, so without it the
            /// `window[name]` bridges vanish and the extension's payload callback never fires.
            /// </summary>
            private void SetDocument(long handle, string url, string html)
            {
                // The session owns document loading because it must deliver the markup as a real
                // navigation (document.write breaks React's node references and does not run the
                // JS-interface init scripts).
                var session = ResolveSession(handle);
                // Seed cookies first: the served document's own bundle will request /api/…, and the
                // WAF must see the clearance cookie or it will hijack the page (see SeedSessionCookies).
                SeedSessionCookies(session, url);
                _lastUrl[handle] = url;
                session.SetDocumentAsync(url, html, CancellationToken.None).GetAwaiter().GetResult();
            }

            /// <summary>
            /// JS defining <c>window[name]</c> as a Proxy whose property access yields a callable
            /// that forwards <c>(name, method, args)</c> to the host's <c>__rensaio</c> binding.
            ///
            /// This is the shape the JCEF provider exposed natively through
            /// <c>@JavascriptInterface</c> (<c>window.android.onResponse(...)</c>), so extension JS
            /// needs no change. The idempotence guard uses the handle so a re-run on the same
            /// document is a no-op but a re-created shim for a new session still installs.
            /// </summary>
            private static string BuildInterfaceShim(string name, long handle)
            {
                var encodedName = JsonSerializer.Serialize(name);
                var handleLiteral = handle.ToString(System.Globalization.CultureInfo.InvariantCulture);

                return "(function(){ var n = " + encodedName + ";" +
                    " if (window[n] && window[n].__rensaio === " + handleLiteral + ") return;" +
                    " var t = { __rensaio: " + handleLiteral + " };" +
                    " window[n] = new Proxy(t, { get: function(target, prop) {" +
                    "   if (prop === '__rensaio') return target.__rensaio;" +
                    "   return function() {" +
                    "     var args = Array.prototype.slice.call(arguments);" +
                    "     return window.__rensaio(JSON.stringify({" +
                    "       h: target.__rensaio, n: n, m: String(prop), a: args" +
                    "     }));" +
                    "   };" +
                    " }}); })();";
            }

            private void AddBinding(long handle, string name, object? target)
            {
                var session = ResolveSession(handle);

                // Sourceforge-style extension JS calls `window[name].method(args...)`. Playwright's
                // ExposeBinding only creates a *function*, so an init script installs a Proxy that
                // turns any property access into a method call routed through the single
                // `__rensaio` binding. This preserves the existing `window.android.onResponse(...)`
                // contract without a JS→JVM hop.
                // The dispatch binding is a plain JS FUNCTION (window.__rensaio), never a
                // window[<interfaceName>] property. That matters: the real KcefWebViewProvider
                // exposes each @JavascriptInterface object as window[name] with its real methods
                // (e.g. window.android.onResponse). A Proxy shim would SHADOW that, breaking the
                // genuine interface surface, and it duplicated what the host already exposes.
                // The Kotlin provider therefore installs the window[name].method(...) shim itself
                // and routes to this single binding.
                if (_bindingSessions.TryAdd(handle, true))
                {
                    // AWAIT the exposure: the page's inline bootstrap can call window[name] during
                    // the very next navigation, so an un-awaited expose loses the callback.
                    session.AddBindingAsync("__rensaio", DispatchToTarget).GetAwaiter().GetResult();
                }

                // Install the window[name] shim as an INIT SCRIPT. It must NOT be an evaluate after
                // navigate: the driver either destroys the execution context mid-call
                //   ("Execution context was destroyed, most likely because of a navigation")
                // or the script lands on a document that is immediately replaced, so window[name]
                // is gone again by the time the extension calls it. Init scripts run at document
                // start for every navigated document, which is exactly the "window[name] always
                // exists" contract the JS expects.
                try
                {
                    session
                        .AddInitScriptAsync(BuildInterfaceShim(name, handle), CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to install the '{Name}' interface shim.", name);
                }

                _targets[(handle, name)] = target;
            }

            /// <summary>
            /// Dispatches `__rensaio(interfaceName, method, argsJson)` to the registered JVM object
            /// via reflection (C#→Java). Returns a JSON-encoded result or "null".
            /// </summary>
            private Task<object?> DispatchToTarget(string payload)
            {
                // The final hop: the page calling `window[bridgeName].post(...)` -> window.__rensaio
                // -> here.
                //
                // INFO, deliberately: this is THE decisive signal for the intermittent
                // "Timed out waiting for WebView". The extension times out only when its payload
                // callback never fires, so the question is binary — did the page ever call back?
                //   - line ABSENT during a timeout -> the page's bootstrap never invoked
                //     window[bridgeName].post(...): the shim was missing, or the served document was
                //     not the extension's markup (so the bootstrap never ran). The failure is upstream
                //     of the binding and the probe's `hasRensaiBinding:"function"` is a red herring.
                //   - line PRESENT but the extension still times out -> the callback DID reach the JVM;
                //     the failure is inside the extension's own payload handling, not the browser layer.
                // Low volume (fires once per payload delivery), so INFO costs nothing.
                _logger.LogInformation("[bridge] dispatch received ({Length} bytes)", payload?.Length ?? 0);

                try
                {
                    var envelope = JsonSerializer.Deserialize<BindingEnvelope>(payload, JsonOptions);
                    if (envelope is null || !_targets.TryGetValue((envelope.H, envelope.N), out var target) || target is null)
                    {
                        return Task.FromResult<object?>("null");
                    }

                    // The proxy faces the JVM here: the handler's result must be a Java type, so
                    // the JSON string is returned as-is (java.lang.String) — never a .NET string.

                    var args = envelope.A ?? [];

                    var method = target.GetType().GetMethods()
                        .FirstOrDefault(m => m.Name == envelope.M && m.GetParameters().Length == args.Length);
                    if (method is null)
                    {
                        _logger.LogDebug("No JVM method '{Method}' on binding '{Name}'.", envelope.M, envelope.N);
                        return Task.FromResult<object?>("null");
                    }

                    var converted = method.GetParameters()
                        .Select((p, i) => ConvertArgument(args[i], p.ParameterType))
                        .ToArray();

                    // What we are about to hand the extension. For `jsBridge { resolve(it) }` this
                    // invocation IS the resume of a suspended coroutine, so the argument value and
                    // type matter. Debug level (see RENSAIO_RPC_VERBOSE).
                    _logger.LogDebug(
                        "[bridge] invoke {Name}.{Method}({ArgTypes}) payload={PayloadLength}",
                        envelope.N,
                        envelope.M,
                        string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)),
                        converted.Length > 0 ? converted[0]?.ToString()?.Length ?? 0 : 0);

                    var result = method.Invoke(target, converted);
                    _logger.LogDebug(
                        "[bridge] invoke returned {ResultType}",
                        result?.GetType().FullName ?? "null");
                    return Task.FromResult<object?>(JsonSerializer.Serialize(result, JsonOptions));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JS binding dispatch failed.");
                    return Task.FromResult<object?>("null");
                }
            }

            private static object? ConvertArgument(object? value, Type targetType)
            {
                if (value is null)
                {
                    return null;
                }
                if (targetType == typeof(string))
                {
                    return value.ToString();
                }
                if (value is JsonElement element)
                {
                    return element.Deserialize(targetType, JsonOptions);
                }
                try
                {
                    return Convert.ChangeType(value, targetType);
                }
                catch (Exception)
                {
                    // Cross-boundary primitive: a number parsed from JSON arrives as a boxed
                    // 'long'/'int', while the JVM method's parameter may be a different numeric
                    // type. Fall back to a string round-trip, which works for the numeric and
                    // boolean shapes these binding methods use.
                    return Convert.ChangeType(
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        targetType,
                        System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            private byte[] Screenshot(long handle)
            {
                var session = ResolveSession(handle);
                return session.ScreenshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            private void SyncCookies(string url, string cookiesJson)
            {
                var cookies = JsonSerializer.Deserialize<BrowserCookie[]>(cookiesJson, JsonOptions) ?? [];
                if (cookies.Length == 0)
                {
                    return;
                }

                var session = _service
                    .OpenSessionAsync(null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                try
                {
                    session.SetCookiesAsync(url, cookies, CancellationToken.None).GetAwaiter().GetResult();
                }
                finally
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            private IBrowserSession ResolveSession(long handle) =>
                _sessions.TryGetValue(handle, out var session)
                    ? session
                    : throw new InvalidOperationException($"Unknown browser session handle {handle}.");

            /// <summary>
            /// Invokes a JVM object's `onResponse(String)` / `onError(String)` method from C#.
            /// This is the C#→Java direction, which is the proven-safe one (no foreign native
            /// thread ever calls into IKVM).
            /// </summary>
            private Task<object?> InvokeJvmTarget(object? target, string bindingName, string payload)
            {
                if (target is null)
                {
                    return Task.FromResult<object?>(null);
                }

                try
                {
                    var type = target.GetType();
                    var method = type.GetMethod("onResponse", new[] { typeof(string) });
                    var result = method is not null
                        ? method.Invoke(target, new object?[] { payload })
                        : type.GetMethod("invoke", new[] { typeof(string) })?.Invoke(target, new object?[] { payload });

                    return Task.FromResult(result);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JS binding '{Binding}' target invocation failed.", bindingName);
                    return Task.FromResult<object?>(null);
                }
            }

            /// <summary>
            /// Wire format for a JS binding call:
            /// <c>{ h: handle, n: interfaceName, m: method, a: [args] }</c>.
            /// </summary>
            private sealed class BindingEnvelope
            {
                [System.Text.Json.Serialization.JsonPropertyName("h")]
                public long H { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("n")]
                public string N { get; set; } = "";

                [System.Text.Json.Serialization.JsonPropertyName("m")]
                public string M { get; set; } = "";

                [System.Text.Json.Serialization.JsonPropertyName("a")]
                public object?[]? A { get; set; }
            }

            private static string AsString(object?[]? args, int index) =>
                args is not null && args.Length > index ? args[index]?.ToString() ?? "" : "";

            private static long AsLong(object?[]? args, int index)
            {
                if (args is null || args.Length <= index || args[index] is null)
                {
                    return 0L;
                }

                try
                {
                    return Convert.ToInt64(args[index]);
                }
                catch (Exception)
                {
                    // A Java `long` parameter (the Tier-2 handle) can arrive as java.lang.Long,
                    // which IConvertible may reject; a string round-trip is safe for this range.
                    return long.TryParse(
                        Convert.ToString(args[index], System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed)
                        ? parsed
                        : 0L;
                }
            }
        }
    }
}
