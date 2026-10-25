using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// One isolated browser context driven by Playwright/Patchright. Implements the Tier-1 solver
    /// path (navigate + in-page <c>fetch</c>) and the Tier-2 live-DOM surface (evaluate, bindings).
    /// </summary>
    public sealed class PlaywrightBrowserSession : IBrowserSession
    {
        private readonly IBrowserContext _context;
        private readonly IBrowser? _browser;
        private readonly bool _stealth;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Func<string, Task<object?>>> _pendingBindings = new();
        private readonly ConcurrentDictionary<string, bool> _registeredBindings = new();

        /// <summary>
        /// Init scripts, kept so they can be re-applied to an ALREADY-LOADED document.
        /// Context-level registration alone only takes effect on the next navigation.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _initScripts = new();

        private IPage? _page;
        private bool _disposed;

        public PlaywrightBrowserSession(
            IBrowserContext context,
            IBrowser? browser,
            bool stealth,
            ILogger logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _browser = browser;
            _stealth = stealth;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<BrowserResponse> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(_disposed, this);

            var timeoutMs = Math.Max(1, request.TimeoutSeconds) * 1000;
            var page = await GetPageAsync(ct).ConfigureAwait(false);

            try
            {
                await EnsureBindingsAsync(page).ConfigureAwait(false);

                // Seed the session from the caller's cookie store BEFORE the fetch so a cleared
                // challenge (cf_clearance) is sent on the in-page request too.
                if (request.Cookies.Count > 0)
                {
                    await SetCookiesAsync(request.Url, request.Cookies, ct).ConfigureAwait(false);
                }

                // Establish origin context (mirrors loadDataWithBaseURL in the old interceptor).
                var contextUrl = request.ContextUrl ?? BuildOrigin(request.Url);
                if (!string.IsNullOrWhiteSpace(contextUrl))
                {
                    await page.GotoAsync(
                        contextUrl,
                        new PageGotoOptions
                        {
                            Timeout = timeoutMs,
                            WaitUntil = MapWaitUntil(request.WaitUntil),
                        }).ConfigureAwait(false);
                }

                var script = BuildFetchScript(request);
                var raw = await page.EvaluateAsync<string>(script).ConfigureAwait(false);

                var payload = JsonSerializer.Deserialize<FetchPayload>(raw);
                if (payload is null)
                {
                    return new BrowserResponse { Error = "browser returned an empty fetch payload" };
                }

                if (!string.IsNullOrEmpty(payload.error))
                {
                    return new BrowserResponse { Error = payload.error };
                }

                var cookies = await GetCookiesAsync(request.Url, ct).ConfigureAwait(false);

                return new BrowserResponse
                {
                    StatusCode = payload.status,
                    StatusMessage = payload.statusText ?? "",
                    Headers = payload.headers ?? new Dictionary<string, string>(),
                    Body = DecodeBase64(payload.bodyBase64),
                    Cookies = cookies,
                    UserAgent = request.Headers.TryGetValue("User-Agent", out var ua) ? ua : null,
                };
            }
            catch (TimeoutException ex)
            {
                return new BrowserResponse { Error = $"timeout: {ex.Message}" };
            }
            catch (PlaywrightException ex)
            {
                return new BrowserResponse { Error = ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task NavigateAsync(string url, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await GetPageAsync(ct).ConfigureAwait(false);
            await EnsureBindingsAsync(page).ConfigureAwait(false);

            // Return at COMMIT, not Load. The caller fires onPageStarted immediately after this,
            // and a real WebView fires onPageStarted at commit — before the document's own
            // deferred/module scripts run. Waiting for Load here (as before) meant the extension's
            // document-start capture script was evaluated only after the page's bundle had already
            // issued its API request, so the hook was installed too late. Load is awaited separately
            // via WaitForLoadAsync.
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit })
                .ConfigureAwait(false);

            // Init scripts registered via the CONTEXT run on every navigation, but a binding
            // registered against the *page* was still installed lazily above. Re-assert both here
            // so a WebView that registers its interfaces before loadUrl (the usual order: WebView is
            // constructed, addJavascriptInterface is called, then loadUrl) still has
            // `window[name]` present on the freshly-loaded document. Without this, extension JS
            // calling `window.android.onResponse(...)` silently hit `undefined` and the caller
            // reported "Timed out waiting for WebView".
            await EnsureBindingsAsync(page).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task WaitForLoadAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await GetPageAsync(ct).ConfigureAwait(false);

            try
            {
                // BOUNDED and NON-FATAL. A real WebView reports onPageFinished even when a
                // sub-resource never settles, and the extension's payload poll does NOT depend on
                // the load event — its capture hook is installed at document-start (during parse),
                // so the payload arrives independently of load. Observed live: comix's served
                // document reaches readyState "interactive" and then a WAF-stalled sub-resource
                // (the module bundle) never resolves, so `load` never fires and an unbounded wait
                // hung the whole request for 60s. Timing out here must therefore be a debug-level
                // no-op, not a failure.
                await page.WaitForLoadStateAsync(
                    LoadState.Load,
                    new PageWaitForLoadStateOptions { Timeout = LoadWaitTimeoutMs })
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug(
                    "WaitForLoadAsync timed out after {Timeout}ms; proceeding (document still interactive). " +
                    "The payload capture runs at document-start and does not depend on the load event.",
                    LoadWaitTimeoutMs);
            }

            await ReapplyInitScriptsAsync(page, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Upper bound for the post-commit load wait. Deliberately short: the payload capture is a
        /// document-start concern, so this wait only exists to sequence onPageFinished after the
        /// document's own resources have (mostly) settled — it must never gate payload delivery.
        /// </summary>
        private const float LoadWaitTimeoutMs = 15_000;

        /// <inheritdoc />
        public async Task<string?> EvaluateAsync(string script, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await GetPageAsync(ct).ConfigureAwait(false);
            await EnsureBindingsAsync(page).ConfigureAwait(false);

            try
            {
                var raw = await page.EvaluateAsync<object?>(script).ConfigureAwait(false);

                // Distinguish "returned null" from "returned empty" (previously `?? ""` collapsed
                // both). Debug level — enable RENSAIO_RPC_VERBOSE=1 when diagnosing.
                _logger.LogDebug(
                    "evaluate returned {Kind}{Length}",
                    raw is null ? "null" : raw.GetType().Name,
                    raw is null ? string.Empty : $" len={raw.ToString()?.Length ?? 0}");

                return raw?.ToString();
            }
            catch (PlaywrightException ex) when (IsNavigationRace(ex))
            {
                // The document was replaced mid-evaluate. One retry on the settled document is
                // enough for the pre-fetch shims and avoids surfacing a spurious failure for a
                // racing redirect. Not retried forever: a page that navigates in a loop must
                // surface as an error rather than spin.
                await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
                return await page.EvaluateAsync<string?>(script).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// True when a Playwright failure is the benign "the page navigated while we were talking to
        /// it" case, which is retryable, as opposed to a real scripting error.
        /// </summary>
        private static bool IsNavigationRace(PlaywrightException ex) =>
            ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Replaces the current document's content (the <c>loadDataWithBaseURL</c> path) and then
        /// RE-APPLIES the registered init scripts.
        ///
        /// The re-application is essential: <c>document.write</c> swaps the document WITHOUT a
        /// navigation, so Playwright never re-runs context init scripts, and the
        /// <c>window[name]</c> JS interfaces the extension calls vanish. That silently broke
        /// payload delivery — the page called <c>window[…].post(…)</c> on an undefined object, the
        /// bridge callback never fired, and the caller saw "Timed out waiting for WebView".
        /// </summary>
        public async Task SetDocumentAsync(string url, string html, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await GetPageAsync(ct).ConfigureAwait(false);
            await EnsureBindingsAsync(page).ConfigureAwait(false);

            var target = string.IsNullOrWhiteSpace(url) ? "about:blank" : url;

            // What exactly is being served? For the comix live-DOM path the open question is
            // whether the served markup is the site's real app shell (which would run its module
            // bundle and issue /api/v1/manga) or something inert (a challenge/stub). This logs the
            // document length, its script sources, and whether it carries WAF/Next markers — enough
            // to identify it without dumping the whole body.
            try
            {
                // A Content-Security-Policy on the served document is the prime suspect when the
                // markup's module script exists but never executes/requests: a `script-src`/`module`
                // restriction silently blocks it. Surface it explicitly.
                var cspIndex = html.IndexOf("Content-Security-Policy", StringComparison.OrdinalIgnoreCase);
                var csp = cspIndex >= 0
                    ? html.Substring(cspIndex, Math.Min(300, html.Length - cspIndex))
                    : "(none)";

                _logger.LogInformation(
                    "[setDocument html] len={Len} hasWaf={Waf} hasNext={Next} hasCsp={Csp} scripts=[{Scripts}]",
                    html.Length,
                    html.Contains("@waf", StringComparison.OrdinalIgnoreCase) ||
                        html.Contains("Security check", StringComparison.OrdinalIgnoreCase),
                    html.Contains("_next", StringComparison.OrdinalIgnoreCase),
                    cspIndex >= 0,
                    DescribeScripts(html));

                if (cspIndex >= 0)
                {
                    _logger.LogInformation("[setDocument csp] {Csp}", csp);
                }

                // Dump the served markup's head (the inline bootstrap + script tags are what the
                // whole payload contract hinges on). Debug: it is large and only needed when
                // diagnosing (RENSAIO_RPC_VERBOSE=1).
                var snippetLen = Math.Min(4000, html.Length);
                _logger.LogDebug("[setDocument body] {Body}", html.Substring(0, snippetLen));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[setDocument html] introspection failed (ignored).");
            }

            // Route accounting. `servedWithHtml` is the decisive signal for the intermittent
            // "Timed out waiting for WebView" after this probe: if it is still 0 once the
            // navigation has settled, our markup was NEVER served (the route did not fulfil) and the
            // real network document survived — which is exactly the state where the extension's
            // payload bootstrap never runs and its poll expires.
            var routeHits = 0;
            var servedWithHtml = 0;

            // DIAGNOSTIC: remember the `fetch` function as it exists at document START (i.e. right
            // after the extension's own document-start interceptors have wrapped it). If the page's
            // bundle later replaces window.fetch, our hook is silently discarded — which would
            // explain "request made, 200 returned, payload never captured". Comparing identity in
            // the probe proves or disproves that in one shot.
            try
            {
                await _context.AddInitScriptAsync(
                    "(function(){try{window.__rensaioFetchAtStart=window.fetch;}catch(e){}})();")
                    .ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug(ex, "fetch-identity probe init script failed (ignored).");
            }

            // NOTE: a per-document fresh page (and an about:blank reset) were tried here and REVERTED
            // — they made later pages WORSE: with a fresh page, page 2 loaded only the cross-origin
            // font CSS (resourceCount:1) and never ran its own module bundle (appRootChildren:0),
            // whereas the reused page at least loaded the page's own CSS. The reused page is kept.
            //
            // Serve the markup AS THE RESPONSE of a real navigation.
            //
            // The previous implementation used document.open/write/close. That is wrong here on two
            // counts, both proven by the logs:
            //   1. It swaps the node tree out from under the page's framework. comix.to's React
            //      bundle then threw "Failed to execute 'removeChild' on 'Node': The node to be
            //      removed is not a child of this node" and never completed its data fetch, so the
            //      payload the caller was polling for could never appear.
            //   2. It is not a navigation, so context init scripts (the window[name] JS interfaces)
            //      did not re-run and the page's callbacks into the host no-op'd.
            // Fulfilling the navigation avoids both: the document is produced by a genuine
            // navigation, which React hydrates normally and which runs init scripts.
            await page.RouteAsync(target, async route =>
            {
                // ONLY the top-level document navigation may be served with our markup. A plain
                // URL pattern matches *any* request for that URL — including a same-URL sub-resource
                // (a fetch/prefetch, an iframe, a service-worker request) that the still-running real
                // page can issue between RouteAsync and the main GotoAsync. When that happened, the
                // single fulfilment slot was consumed by the non-document request and the real page
                // won the navigation: the DOM stayed the site's own page (large), the extension's
                // bootstrap never ran, and the caller reported "Timed out waiting for WebView" — the
                // intermittent failure this probe follows. Gating on a main-frame navigation removes
                // that race entirely.
                var frame = route.Request.Frame;
                var isMainDocumentNavigation =
                    route.Request.IsNavigationRequest &&
                    frame is not null &&
                    frame.ParentFrame is null;

                if (!isMainDocumentNavigation)
                {
                    await route.ContinueAsync().ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref routeHits);

                if (Interlocked.Exchange(ref servedWithHtml, 1) == 1)
                {
                    // A later main-frame navigation for this URL (a redirect) goes to the network;
                    // only the first one is ours.
                    await route.ContinueAsync().ConfigureAwait(false);
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html; charset=utf-8",
                    Body = InjectCaptureHook(target, html),
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            try
            {
                // Commit, not Load: the caller fires onPageStarted right after this returns, so a
                // document-start script the extension evaluates from it lands BEFORE the served
                // document's deferred/module scripts (which are what issue /api/v1/manga). Load is
                // awaited separately via WaitForLoadAsync. Unrouting at commit is safe: the route
                // has already served the document, and leaving it registered until Load would risk
                // intercepting a same-URL sub-resource the settled page may request.
                await page.GotoAsync(target, new PageGotoOptions { WaitUntil = WaitUntilState.Commit })
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await page.UnrouteAsync(target).ConfigureAwait(false);
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogDebug(ex, "Unroute after setDocument failed (ignored).");
                }
            }

            // DIAGNOSTIC: report the served document's actual state.
            //
            // Repeated attempts to infer why the page never produced its payload have all been
            // guesses. This probe answers it directly: is the markup there, did its scripts run,
            // and is the host bridge visible to the page? Logged at Information so the level
            // plumbing cannot hide it.
            try
            {
                var probe = await page.EvaluateAsync<string>(
                    @"(function () {
                        var out = {
                            readyState: document.readyState,
                            url: location.href,
                            htmlLength: document.documentElement ? document.documentElement.outerHTML.length : 0,
                            hasInitialData: !!document.querySelector('script#initial-data'),
                            initialDataLength: (document.querySelector('script#initial-data') || {}).textContent ? document.querySelector('script#initial-data').textContent.length : 0,
                            scriptCount: document.scripts.length,
                            hasRensaiBinding: typeof window.__rensaio,
                            moduleScripts: Array.prototype.filter.call(document.scripts, function (s) { return s.type === 'module'; }).length,
                            captureInstalled: !!window.__comixBrowseCaptureInstalled,
                            browsePayloadPresent: !!window.__comixBrowsePayload,
                            // If true, the page's bundle replaced window.fetch AFTER the extension's
                            // document-start hook was installed, so its interceptor never saw the
                            // (successful) /api/v1/manga response.
                            fetchReplacedByPage: (typeof window.__rensaioFetchAtStart !== 'undefined') &&
                                (window.fetch !== window.__rensaioFetchAtStart),
                            // Any script/module load or uncaught runtime error captured at document
                            // start. Non-empty => the bundle failed to load/execute; empty => the
                            // bundle ran but chose not to fetch.
                            scriptErrors: (window.__rensaioScriptErrors || []).slice(0, 5).join(' || '),
                            initialDataQueries: (function () {
                                try {
                                    var el = document.querySelector('script#initial-data');
                                    if (!el) return 'NO_ELEMENT';
                                    var parsed = JSON.parse(el.textContent);
                                    if (!parsed || !parsed.queries) return 'NO_QUERIES';
                                    var keys = Object.keys(parsed.queries);
                                    var hasItems = keys.some(function (k) {
                                        var v = parsed.queries[k];
                                        return v && (v.result && Array.isArray(v.result.items) || Array.isArray(v.items));
                                    });
                                    return JSON.stringify({ keys: keys, hasResultItems: hasItems });
                                } catch (e) {
                                    return 'ERR:' + e.message;
                                }
                            })()
                        };
                        return JSON.stringify(out);
                    })()").ConfigureAwait(false);

                // INFO, not Debug: this one line answers "is the binding the extension depends on
                // actually present on the served document?" — the question every payload timeout
                // hinges on. Without verbose mode it was invisible.
                _logger.LogInformation("[setDocument probe] {Probe}", probe);

                // Decisive companion line to the probe. The probe reports the DOM the extension will
                // actually see; this reports whether that DOM is OURS.
                //   servedByRoute:false after a completed Goto  -> the route never fulfilled, so the
                //   network document (the real site page) is what the extension is looking at and the
                //   payload bootstrap cannot have run. That is the root of the timeout, not a binding
                //   problem (the probe's hasRensaiBinding stays "function" either way).
                //   routeHits > 1 with servedByRoute:true -> a redirect re-navigated; harmless.
                _logger.LogInformation(
                    "[setDocument route] servedByRoute={Served} routeHits={Hits} target={Target}",
                    servedWithHtml == 1,
                    routeHits,
                    target);

                // DELAYED resource/execution probe. The immediate probe runs at commit, before the
                // served document's sub-resources load. This one waits, then reports what ACTUALLY
                // loaded and whether the module bundle ran — the decisive question when a page
                // commits but never issues /api (page 1 vs page 2). Run off the page's own timers
                // (Task.Delay, not page.WaitForTimeout) so it cannot be blocked by the page state.
                await Task.Delay(4000).ConfigureAwait(false);
                try
                {
                    var resources = await page.EvaluateAsync<string>(
                        @"(function () {
                            try {
                                var perf = (performance.getEntriesByType('resource') || []).map(function (e) { return e.name; });
                                var scripts = Array.prototype.map.call(document.scripts, function (s) { return s.src || 'inline'; });
                                var appRoot = document.getElementById('app-root');
                                var links = Array.prototype.map.call(
                                    document.querySelectorAll('link[rel=stylesheet]'),
                                    function (l) { return l.getAttribute('href'); });
                                var mods = Array.prototype.map.call(
                                    document.querySelectorAll('script[type=module]'),
                                    function (s) { return s.getAttribute('src'); });
                                // Resources still in flight (responseEnd===0) block the load event.
                                var pending = (performance.getEntriesByType('resource') || [])
                                    .filter(function (e) { return !e.responseEnd; })
                                    .map(function (e) { return e.name; });
                                return JSON.stringify({
                                    readyState: document.readyState,
                                    resourceCount: perf.length,
                                    pendingCount: pending.length,
                                    pending: pending.slice(0, 10),
                                    resources: perf.slice(0, 15),
                                    scriptSrcs: scripts,
                                    linkCount: links.length,
                                    linkHrefs: links,
                                    moduleCount: mods.length,
                                    moduleSrcs: mods,
                                    hookInstalled: !!window.__rensaioCaptureHookInstalled,
                                    appRootChildren: appRoot ? appRoot.children.length : -1,
                                    browsePayloadPresent: !!window.__comixBrowsePayload,
                                    captureInstalled: !!window.__comixBrowseCaptureInstalled
                                });
                            } catch (e) { return 'ERR:' + e.message; }
                        })()").ConfigureAwait(false);
                    _logger.LogInformation("[setDocument probe2] {Probe}", resources);
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogDebug(ex, "[setDocument probe2] failed (ignored).");
                }
            }
            catch (PlaywrightException ex)
            {
                _logger.LogWarning(ex, "[setDocument probe] failed");
            }
        }

        /// <inheritdoc />
        public async Task AddInitScriptAsync(string script, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Context-level: Playwright re-runs these on every page/frame navigation, which is what
            // the `window[name]` Proxy shim needs to survive page loads.
            await _context.AddInitScriptAsync(script).ConfigureAwait(false);
            _initScripts.Add(script);
        }

        /// <summary>
        /// Applies every registered init script to the CURRENT document. Context-level init scripts
        /// only run on the next navigation, so a page that is already loaded (the WebView factory
        /// creates the page, then registers interfaces) would otherwise never see them.
        /// </summary>
        private async Task ReapplyInitScriptsAsync(IPage page, CancellationToken ct)
        {
            if (_initScripts.IsEmpty)
            {
                return;
            }

            foreach (var script in _initScripts)
            {
                try
                {
                    await page.EvaluateAsync(script).ConfigureAwait(false);
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogDebug(ex, "Re-applying an init script to the current page failed.");
                }
            }
        }

        /// <inheritdoc />
        public async Task AddBindingAsync(string name, Func<string, Task<object?>> handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(handler);

            _pendingBindings[name] = handler;
            // If a page already exists, register immediately AND AWAIT it; otherwise it is
            // registered (and awaited) lazily by EnsureBindingsAsync. Awaiting here is what removes
            // the "binding not yet installed when the page's inline bootstrap calls it" race.
            if (_page is not null)
            {
                await RegisterBindingAsync(_page, name, handler).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var cookies = string.IsNullOrWhiteSpace(url)
                ? await _context.CookiesAsync().ConfigureAwait(false)
                : await _context.CookiesAsync(new[] { url }).ConfigureAwait(false);

            return cookies.Select(c => new BrowserCookie(
                c.Name,
                c.Value,
                c.Domain ?? "",
                c.Path ?? "/",
                c.Secure,
                c.HttpOnly,
                c.Expires >= 0 ? (double)c.Expires : null)).ToArray();
        }

        /// <inheritdoc />
        public async Task SetCookiesAsync(
            string url,
            IReadOnlyCollection<BrowserCookie> cookies,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(cookies);

            var toAdd = cookies.Select(c => new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = string.IsNullOrWhiteSpace(c.Domain) ? null : c.Domain,
                Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
                Url = string.IsNullOrWhiteSpace(c.Domain) ? url : null,
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
                Expires = c.Expires.HasValue ? (float)c.Expires.Value : null,
            }).ToArray();

            if (toAdd.Length > 0)
            {
                await _context.AddCookiesAsync(toAdd).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<byte[]> ScreenshotAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = await GetPageAsync(ct).ConfigureAwait(false);
            return await page.ScreenshotAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing the browser context.");
            }

            if (_browser is not null)
            {
                try
                {
                    await _browser.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error while closing the browser.");
                }
            }
        }

        private async Task<IPage> GetPageAsync(CancellationToken ct)
        {
            if (_page is not null)
            {
                return _page;
            }

            _page = await _context.NewPageAsync().ConfigureAwait(false);
            _page.SetDefaultTimeout(60_000);

            // Forward the PAGE's own console output and uncaught errors into the host log.
            //
            // Without this, everything the page does — CSP violations, blocked inline scripts,
            // failed module imports, rejected promises — is invisible, and a caller that times out
            // gives no clue why. Page errors are the single most useful signal for a
            // document.write-based WebView flow, because CSP and module-script restrictions only
            // manifest there.
            // Page diagnostics at Debug (enable RENSAIO_RPC_VERBOSE=1). Page ERRORS stay at Warning:
            // an uncaught exception in the page is a real fault signal, not routine telemetry.
            _page.Console += (_, msg) =>
            {
                // Page console stays at Debug EXCEPT for errors: when the extension's callback is
                // missing, the page itself usually logs "window[...] is not a function" /
                // "Cannot read properties of undefined", which is decisive and must be visible.
                if (string.Equals(msg.Type, "error", StringComparison.OrdinalIgnoreCase))
                    _logger.LogWarning("[page:console-error] {Text}", msg.Text);
                else
                    _logger.LogDebug("[page:{Type}] {Text}", msg.Type, msg.Text);
            };
            _page.PageError += (_, error) =>
                _logger.LogWarning("[page:error] {Error}", error);

            // Network telemetry. `/api/` traffic stays at INFO (low volume, and the decisive signal
            // for "did the page fetch its data?"). The DOCUMENT and its sub-resources (scripts,
            // modules, CSS) are logged too — but only for the comix host and only at DEBUG — because
            // the open question for the served `loadDataWithBaseURL` document is whether its own
            // module bundle even EXECUTES and requests anything. If no script/module request is ever
            // logged, the served markup's scripts are not running at all (a far more fundamental
            // problem than capture timing). Enable RENSAIO_RPC_VERBOSE=1 to see these.
            static bool IsComix(string url) =>
                url.Contains("comix.to", StringComparison.OrdinalIgnoreCase);

            // Cloudflare's challenge platform (managed/js-detection) and comix's own WAF route.
            static bool IsChallengeUrl(string url) =>
                url.Contains("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/@waf/challenge", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);

            _page.Request += (_, req) =>
            {
                if (req.Url.Contains("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[page:req] {Method} {Url}", req.Method, req.Url);
                }
                else if (IsChallengeUrl(req.Url))
                {
                    // Debug, not Warn: this fires for EVERY Cloudflare challenge-orchestration script
                    // (chl_page/turnstile/pat/ci/fo), including the ones that mean the challenge is
                    // PASSING — so at Warn it is pure noise. Enable RENSAIO_RPC_VERBOSE=1 to see it.
                    _logger.LogDebug("[page:challenge] Cloudflare challenge script requested: {Url}", req.Url);
                }
                else if (IsComix(req.Url))
                {
                    _logger.LogDebug("[page:req*] {Method} {ResourceType} {Url}", req.Method, req.ResourceType, req.Url);
                }
            };
            _page.Response += (_, res) =>
            {
                if (res.Url.Contains("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[page:res] {Status} {Url}", res.Status, res.Url);
                }
                else if (IsComix(res.Url))
                {
                    _logger.LogInformation("[page:res*] {Status} {Url}", res.Status, res.Url);
                }
            };
            _page.RequestFailed += (_, req) =>
            {
                if (req.Url.Contains("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[page:reqfail] {Url} {Failure}", req.Url, req.Failure);
                }
                else if (IsComix(req.Url))
                {
                    _logger.LogWarning("[page:reqfail*] {Url} {Failure}", req.Url, req.Failure);
                }
            };

            await EnsureBindingsAsync(_page).ConfigureAwait(false);
            return _page;
        }

        /// <summary>
        /// Ensures every pending binding is EXPOsed and AWAITS the exposure. Navigation must not
        /// proceed until this returns: a page whose inline bootstrap calls <c>window[name]</c>
        /// during parse runs concurrently with the assigned navigation, so an un-awaited exposure
        /// loses the callback and the extension times out.
        /// </summary>
        private async Task EnsureBindingsAsync(IPage page)
        {
            foreach (var binding in _pendingBindings)
            {
                await RegisterBindingAsync(page, binding.Key, binding.Value).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Exposes <paramref name="name"/> on the browser CONTEXT and awaits the driver's
        /// acknowledgement. Exposure is deliberately context-level (not page-level): page-scoped
        /// exposure does NOT survive a navigation, so the extension's `window.android.onResponse(...)`
        /// hit `undefined` after the document loaded.
        ///
        /// Registration is idempotent per session; on failure the marker is NOT set, so a later call
        /// retries rather than leaving the session permanently without the binding.
        /// </summary>
        private async Task RegisterBindingAsync(IPage page, string name, Func<string, Task<object?>> handler)
        {
            if (_registeredBindings.ContainsKey(name))
            {
                return;
            }

            try
            {
                await _context.ExposeBindingAsync<string, object?>(
                    name,
                    (BindingSource _, string payload) => handler(payload ?? "").GetAwaiter().GetResult())
                    .ConfigureAwait(false);

                _registeredBindings[name] = true;
                // INFO: whether the callback the extension depends on is actually installable is
                // the single most useful signal when a payload never arrives; keep it visible.
                _logger.LogInformation(
                    "Exposed binding '{Binding}' on browser context for page {Url}.",
                    name,
                    page.Url);
            }
            catch (Exception ex)
            {
                _registeredBindings.TryRemove(name, out _);
                _logger.LogWarning(ex, "Failed to expose binding '{Binding}'; will retry on next use.", name);
            }
        }

        /// <summary>
        /// Summarises a document's <c><script></c> tags for logging: each entry is the
        /// <c>src</c> value (or <c>"inline"</c>), capped at a handful so the log line stays readable.
        /// </summary>
        private static string DescribeScripts(string html)
        {
            var results = new List<string>();
            var index = 0;
            while (results.Count < 8)
            {
                var start = html.IndexOf("<script", index, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    break;
                }
                var end = html.IndexOf('>', start);
                if (end < 0)
                {
                    break;
                }
                var tag = html.Substring(start, end - start);
                var srcIndex = tag.IndexOf("src=", StringComparison.OrdinalIgnoreCase);
                if (srcIndex >= 0)
                {
                    var quote = tag.IndexOfAny(['"', '\''], srcIndex);
                    if (quote >= 0)
                    {
                        var close = tag.IndexOf(tag[quote], quote + 1);
                        if (close > quote)
                        {
                            results.Add(tag.Substring(quote + 1, close - quote - 1));
                        }
                    }
                }
                else
                {
                    results.Add("inline");
                }
                index = end + 1;
            }
            return string.Join(" | ", results);
        }

        /// <summary>
        /// Inserts a DOCUMENT-START payload-capture hook as the first child of the served document's
        /// <c><head></c>.
        ///
        /// Why this exists: the extension's own capture script is evaluated from <c>onPageStarted</c>,
        /// which fires at/just after navigation commit. The served page's module bundle is a
        /// <c>defer</c> script, so it can execute and issue <c>/api/v1/manga</c> during parse BEFORE
        /// the extension's evaluate reaches the page. Proven by the probe: the page fully mounted and
        /// <c>/api/v1/manga</c> returned 200, yet <c>captureInstalled:false</c> — the hook was
        /// installed too late and missed the response, so no payload was delivered and the extension
        /// timed out. That is the intermittency: the hook/bundle race decides the outcome.
        ///
        /// This hook runs during parse (classic inline script, ahead of the deferred module), wraps
        /// <c>fetch</c>/<c>XHR</c>, matches <c>/api/v1/manga</c>, stores the payload under the key the
        /// extension polls (<c>__comixBrowsePayload</c>) and pushes it through the extension's own
        /// bridge (discovered via the <c>__rensaio</c> marker on the host's shim). The extension's
        /// script short-circuits on an already-present payload, so setting the key early is enough.
        ///
        /// Gated to the comix origin: it encodes comix's API path and the extension's payload key.
        /// </summary>
        private static string InjectCaptureHook(string url, string html)
        {
            if (string.IsNullOrEmpty(html) ||
                url.IndexOf("comix.to", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return html;
            }

            const string hook = """
                <script>
                (function () {
                    if (window.__rensaioCaptureHookInstalled) return;
                    window.__rensaioCaptureHookInstalled = true;
                    var KEY = '__comixBrowsePayload';
                    var deliver = function (text) {
                        try {
                            if (!text) return;
                            var parsed = JSON.parse(text);
                            if (parsed && Array.isArray(parsed.items)) parsed = { result: parsed };
                            if (!(parsed && parsed.result && Array.isArray(parsed.result.items)
                                && parsed.result.items.length > 0)) return;
                            window[KEY] = JSON.stringify(parsed);
                            for (var k in window) {
                                try {
                                    var w = window[k];
                                    if (w && w.__rensaio && typeof w.post === 'function') { w.post(window[KEY]); break; }
                                } catch (e) { }
                            }
                        } catch (e) { }
                    };
                    var shouldCapture = function (rawUrl) {
                        try { return new URL(rawUrl || '', window.location.origin).pathname.indexOf('/api/v1/manga') >= 0; }
                        catch (e) { return false; }
                    };
                    var originalFetch = window.fetch;
                    if (typeof originalFetch === 'function') {
                        window.fetch = function () {
                            return originalFetch.apply(this, arguments).then(function (response) {
                                try {
                                    if (shouldCapture(response && response.url)) {
                                        response.clone().text().then(deliver).catch(function () { });
                                    }
                                } catch (e) { }
                                return response;
                            });
                        };
                    }
                    var originalOpen = XMLHttpRequest.prototype.open;
                    var originalSend = XMLHttpRequest.prototype.send;
                    XMLHttpRequest.prototype.open = function (method, u) {
                        this.__rensaioCaptureUrl = String(u || '');
                        return originalOpen.apply(this, arguments);
                    };
                    XMLHttpRequest.prototype.send = function () {
                        var xhr = this;
                        xhr.addEventListener('load', function () {
                            try { if (shouldCapture(xhr.__rensaioCaptureUrl)) deliver(xhr.responseText); } catch (e) { }
                        });
                        return originalSend.apply(this, arguments);
                    };
                })();
                </script>
                """;

            var headIndex = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (headIndex >= 0)
            {
                var headClose = html.IndexOf('>', headIndex);
                if (headClose >= 0)
                {
                    return html.Insert(headClose + 1, hook);
                }
            }

            return hook + html;
        }

        private static string BuildOrigin(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Authority}/";
            }
            return url;
        }

        private static WaitUntilState MapWaitUntil(string? waitUntil) => waitUntil switch
        {
            "load" => WaitUntilState.Load,
            "domcontentloaded" => WaitUntilState.DOMContentLoaded,
            "networkidle0" or "networkidle" => WaitUntilState.NetworkIdle,
            _ => WaitUntilState.Load,
        };

        private static byte[] DecodeBase64(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return [];
            }
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return [];
            }
        }

        /// <summary>
        /// Builds the in-page fetch script. It mirrors the semantics of the old
        /// <c>WebViewFetchInterceptor</c> JS: perform the fetch, read the body as base64, capture
        /// the status/headers and return a JSON envelope (no JS binding round-trip needed).
        /// </summary>
        private static string BuildFetchScript(BrowserRequest request)
        {
            var url = JsonSerializer.Serialize(request.Url);
            var method = JsonSerializer.Serialize(request.Method);
            var headers = JsonSerializer.Serialize(request.Headers);
            var body = JsonSerializer.Serialize(request.Body ?? "");
            var credentials = JsonSerializer.Serialize("include");

            return $$"""
                (async () => {
                    try {
                        const url = {{url}};
                        const method = {{method}};
                        const headersObj = {{headers}};
                        const bodyString = {{body}};

                        const headers = new Headers();
                        for (const [key, value] of Object.entries(headersObj)) {
                            headers.append(key, value);
                        }

                        const response = await fetch(url, {
                            method: method,
                            headers: headers,
                            body: (bodyString && bodyString.length > 0) ? bodyString : undefined,
                            credentials: {{credentials}},
                            cache: 'no-store',
                        });

                        const arrayBuffer = await response.arrayBuffer();
                        const bytes = new Uint8Array(arrayBuffer);
                        let binary = '';
                        for (let i = 0; i < bytes.length; i++) {
                            binary += String.fromCharCode(bytes[i]);
                        }
                        const bodyBase64 = btoa(binary);

                        const headersOut = {};
                        response.headers.forEach((value, key) => { headersOut[key] = value; });

                        return JSON.stringify({
                            status: response.status,
                            statusText: response.statusText,
                            headers: headersOut,
                            bodyBase64: bodyBase64,
                        });
                    } catch (e) {
                        return JSON.stringify({ error: e.toString() });
                    }
                })()
                """;
        }

        private sealed class FetchPayload
        {
            public int status { get; set; }

            public string? statusText { get; set; }

            public Dictionary<string, string>? headers { get; set; }

            public string? bodyBase64 { get; set; }

            public string? error { get; set; }
        }
    }
}
