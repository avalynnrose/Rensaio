package xyz.nulldev.androidcompat.webkit

import android.content.Intent
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Picture
import android.graphics.Rect
import android.graphics.drawable.Drawable
import android.net.Uri
import android.net.http.SslCertificate
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.Message
import android.print.PrintDocumentAdapter
import android.util.Log
import android.util.SparseArray
import android.view.DragEvent
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.PointerIcon
import android.view.View
import android.view.ViewGroup.LayoutParams
import android.view.ViewStructure
import android.view.WindowInsets
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityNodeProvider
import android.view.autofill.AutofillValue
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.textclassifier.TextClassifier
import android.webkit.DownloadListener
import android.webkit.WebBackForwardList
import android.webkit.WebChromeClient
import android.webkit.WebMessage
import android.webkit.WebMessagePort
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebView.HitTestResult
import android.webkit.WebView.PictureListener
import android.webkit.WebView.VisualStateCallback
import android.webkit.WebViewClient
import android.webkit.WebViewProvider
import android.webkit.WebViewProvider.ScrollDelegate
import android.webkit.WebViewProvider.ViewDelegate
import android.webkit.WebViewRenderProcess
import android.webkit.WebViewRenderProcessClient
import extension.bridge.browser.BrowserHostGateway
import extension.bridge.logging.androidCompatLogger
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import android.webkit.CookieHandlerBridge
import java.io.BufferedWriter
import java.io.File
import java.net.URI
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * [WebViewProvider] backed by the .NET-hosted browser (Patchright/Playwright) instead of JCEF.
 *
 * This is the thin Kotlin client described in the plan (§6.2): it maps `loadUrl` /
 * `evaluateJavascript` / JS bindings / cookies onto [BrowserHostGateway] and leaves pooling,
 * retries and provider selection to the C# side. It deliberately mirrors
 * [KcefWebViewProvider]'s lifecycle (`init` → navigate → `onPageFinished` → `destroy`) so
 * extension code that drives a `WebView` needs no change.
 *
 * ## Threading
 * `WebViewProvider` methods are invoked on the emulated Android main looper. The host calls
 * **block**, so they are dispatched to a dedicated single-thread executor and the results are
 * posted back to the main looper — never executed inline on the looper. This is the documented
 * rule from the plan ("`execute` must never be called on the Android main looper").
 *
 * ## JS→JVM direction
 * Avoided. The page calls into .NET; .NET then invokes the JVM object supplied via
 * [addJavascriptInterface] (C#→Java, the proven-safe direction). No foreign native thread ever
 * calls into IKVM.
 */
class PlaywrightWebViewProvider(
    private val view: WebView,
) : WebViewProvider {
    private companion object {
        const val TAG = "PlaywrightWebViewProvider"
    }

    private val logger = androidCompatLogger(PlaywrightWebViewProvider::class.java)
    private val mainHandler = Handler(Looper.getMainLooper())

    /** Serialises host calls so a single session is never driven concurrently. */
    // NOT single-threaded. A `create()` that has to WAIT for a session slot would otherwise occupy
    // the only thread, and `destroy()` — the very thing that frees a slot — is queued behind it on
    // the same executor. That is a deadlock: every waiting WebView would block the release of the
    // session it is waiting for. Host calls are still serialised per WebView by the caller's own
    // dispatchHostCall latch; this executor only needs enough width for create + release to
    // proceed concurrently.
    // SINGLE-threaded, per WebView. This is what guarantees call ORDER: a WebView's calls are a
    // strict sequence (addJavascriptInterface → loadDataWithBaseURL → evaluateJavaScript poll), and
    // a multi-threaded executor let the polls overtake the document load.
    //
    // A FIFO-chain attempt over a 4-thread pool was worse, not better: each chained continuation
    // occupied a pool thread while its inner work queued behind it on the SAME pool, which
    // starved every thread and hung the process (logs stopped entirely, both processes).
    // One thread per WebView is deadlock-free because each WebView's work is strictly sequential,
    // and WebViews still run concurrently with each other.
    private val hostExecutor = Executors.newSingleThreadExecutor { r ->
        Thread(r, "playwright-webview-host").apply { isDaemon = true }
    }

    /** Monotonic dispatch counter used to prove submission vs execution ordering. */
    private val callSeq = AtomicLong(0)

    /**
     * Emulates document-start: invokes [WebViewClient.onPageStarted] synchronously ON THE CALLER's
     * THREAD, with [inDocumentStart] raised for the duration.
     *
     * The thread is what matters. The extension's callback makes its `evaluateJs` calls on this same
     * thread, and [evaluateJavaScript] reads [inDocumentStart] at submission time to decide whether
     * a script is document-start work. Raising the flag on the host executor instead is useless —
     * the extension thread would see `false` and its interceptor script would be queued as an
     * ordinary evaluate, running long after the page's own API calls had already been made.
     */
    private fun notifyPageStarted(url: String) {
        trace("onPageStarted(${url.take(120)})")
        runCatching { viewClient.onPageStarted(view, url, null) }
            .onFailure { logger.warn(it) { "onPageStarted threw" } }
    }

    // NOTE: there is deliberately no "document-start" flag for extension scripts. Routing scripts
    // evaluated from onPageStarted to addInitScript was tried and REVERTED: those scripts call
    // window[passPayloadName], which the page's own inline bootstrap defines, and init scripts run
    // before that bootstrap. See the comment in evaluateJavaScript.

    private val settings = BrowserWebSettings()

    @Volatile private var sessionHandle: Long = 0L
    private val handleGen = AtomicLong(0)
    private val destroyed = AtomicBoolean(false)

    @Volatile private var viewClient: WebViewClient = WebViewClient()
    @Volatile private var chromeClient: WebChromeClient = WebChromeClient()
    @Volatile private var downloadListener: DownloadListener? = null

    /** Interfaces registered before the page exists; applied once the session is created. */
    private val pendingInterfaces = LinkedHashMap<String, Any>()
    private val appliedInterfaces = HashSet<String>()

    @Volatile private var currentUrl: String = ""
    @Volatile private var pageReady = false

    private val viewDelegate = PlaywrightViewDelegate()
    private val scrollDelegate = PlaywrightScrollDelegate()

    // ── Lifecycle ───────────────────────────────────────────────────────────

    override fun init(
        javaScriptInterfaces: Map<String, Any>?,
        privateBrowsing: Boolean,
    ) {
        // Whether a WebView-requiring source reaches the provider AT ALL. If this never appears for
        // a source that times out, the failure is above the browser layer. Emitted via trace, so it
        // needs RENSAIO_RPC_VERBOSE=1 on the RPC side.
        trace(
            "init(privateBrowsing=$privateBrowsing)",
            "interfaces=${javaScriptInterfaces?.keys ?: emptySet()} thread=${Thread.currentThread().name}",
        )

        // Deliberately does NOT create the host session here.
        //
        // One session is a full persistent browser context (its own Chromium profile), so creating
        // it in init() meant every constructed WebView spawned a browser even when it was never
        // used. Extensions construct WebViews liberally — synchronously in their constructor is
        // common — so idle startup launched N contexts at once (observed: four). The session is
        // now created on first actual use (loadUrl / evaluateJavaScript / addJavascriptInterface),
        // which is what the pool and the concurrency cap were always meant to bound.
        javaScriptInterfaces?.forEach { (name, obj) -> pendingInterfaces[name] = obj }
    }

    /**
     * Creates the host session lazily. Returns 0 when no host is installed or it is unavailable,
     * in which case every Tier-2 operation degrades to a no-op (the interceptor's Tier-1 path and
     * the direct OkHttp chain are unaffected).
     */
    private fun ensureSession(): Long {
        if (destroyed.get()) return 0L
        val existing = sessionHandle
        if (existing != 0L) return existing

        if (!BrowserHostGateway.isAvailable()) {
            logger.warn { "Browser host unavailable; Playwright WebView provider is inert" }
            return 0L
        }

        return synchronized(this) {
            if (sessionHandle != 0L) {
                sessionHandle
            } else {
                runCatching { BrowserHostGateway.host().create() }
                    .onSuccess {
                        sessionHandle = it
                        handleGen.incrementAndGet()
                        logger.debug { "Created browser session handle=$it" }
                    }
                    .onFailure { logger.warn(it) { "Failed to create browser session" } }
                    .getOrDefault(0L)
            }
        }
    }

    override fun destroy() {
        if (!destroyed.compareAndSet(false, true)) return
        val handle = sessionHandle
        sessionHandle = 0L
        pageReady = false
        appliedInterfaces.clear()

        // Release the session on the host executor, deliberately NOT inline: destroy() is called
        // from the main looper, and host calls block. C# keys live-DOM sessions by handle, so
        // skipping this leaked a browser context (and its profile directory) for every WebView
        // that was ever destroyed.
        if (handle != 0L && !hostExecutor.isShutdown) {
            runCatching {
                hostExecutor.execute {
                    runCatching { BrowserHostGateway.host().destroy(handle) }
                        .onFailure { logger.debug(it) { "session destroy failed (ignored)" } }
                }
                // Give the executor a moment to drain the release before it is torn down.
                hostExecutor.shutdown()
                hostExecutor.awaitTermination(5, TimeUnit.SECONDS)
            }.onFailure { logger.debug(it) { "session release timed out (ignored)" } }
        } else {
            hostExecutor.shutdownNow()
        }

        logger.debug { "destroyed (handle=$handle)" }
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    override fun loadUrl(
        url: String,
        additionalHttpHeaders: Map<String, String>?,
    ) {
        currentUrl = url
        pageReady = false
        trace(
            "loadUrl(url=${url.take(160)}, headers=${additionalHttpHeaders?.size ?: 0})",
            "thread=${Thread.currentThread().name}",
        )

        // Real WebView ordering, faithfully reproduced:
        //   1. host.navigate(...) returns at COMMIT (document parsed but its deferred/module
        //      scripts have not run yet);
        //   2. onPageStarted is fired NOW, so a document-start script the extension evaluates from
        //      it (e.g. comix's fetch/XHR/JSON.parse capture hook) is installed BEFORE the page's
        //      own bundle issues its API calls;
        //   3. load is awaited in a SEPARATE task (see awaitLoadThenFinish), THEN onPageFinished
        //      fires.
        //
        // The previous code fired onPageStarted in onSuccess, i.e. AFTER the load had settled, so
        // the extension's hook was always installed too late and its payload poll expired
        // ("Timed out waiting for WebView"). It also must NOT fire before host.navigate: that ran
        // the script against the blank/previous document (the reason an earlier attempt was
        // reverted). Commit is the correct point.
        dispatchHostCall(
            onSuccess = { _ -> Unit },
            onFailure = { t ->
                logger.warn(t) { "loadUrl failed for $url" }
                runCatching { viewClient.onReceivedError(view, -1, t.message ?: "loadUrl failed", url) }
            },
            call = { host ->
                val handle = ensureSession()
                if (handle == 0L) {
                    ""
                } else {
                    // Push the JVM cookie store into the host jar first, so the live-DOM session is
                    // seeded with the same cookies OkHttp uses (incl. a solver-obtained cf_clearance).
                    // The host classifies CF cookies and only seeds them when the browser has none.
                    pushJvmCookiesToHost(host, url)
                    val result = host.navigate(handle, url)
                    // Install the window[name] shims BEFORE the document's scripts run.
                    applyInterfaces()
                    // Fire onPageStarted at commit. Its evaluateJavaScript calls are queued on this
                    // same FIFO executor BEHIND this task, so they run during parse — before the
                    // document's deferred/module scripts.
                    notifyPageStarted(url)
                    // Queue the load-wait as a SEPARATE task so it runs AFTER those queued capture
                    // evaluates on the FIFO executor. Waiting inline here would block the executor
                    // until load, pushing the capture evaluates past parse — the exact bug.
                    awaitLoadThenFinish(handle, url)
                    result
                }
            },
        )
    }

    /**
     * Awaits the `load` event of the page started by the most recent navigate/setDocument, then
     * posts `onPageFinished` to the main looper.
     *
     * Runs on a DEDICATED thread, NOT [hostExecutor]. That is essential and was the second half of
     * the fix: [notifyPageStarted] (called in the preceding executor task) queues the extension's
     * capture `evaluateJavaScript` calls onto the FIFO [hostExecutor]; those must run during parse,
     * BEFORE the page's deferred/module scripts. If this load-wait ran on [hostExecutor] it would
     * BLOCK that single thread until `load` (which, on a page with a stalled sub-resource, may be
     * never), so the extension's own payload-poll evaluates queued behind it would never run and
     * the extension would time out — even though its capture hook was installed correctly. Keeping
     * the wait off the executor lets those evaluates execute immediately while the wait proceeds in
     * parallel.
     */
    private fun awaitLoadThenFinish(handle: Long, url: String) {
        if (destroyed.get()) return
        runCatching {
            Thread({
                try {
                    BrowserHostGateway.host().awaitLoad(handle)
                } catch (t: Throwable) {
                    // Bounded + non-fatal by design (see PlaywrightBrowserSession.WaitForLoadAsync):
                    // a stalled sub-resource must not fail the request, and onPageFinished still
                    // fires — matching a real WebView, which reports finish regardless.
                    logger.debug { "awaitLoad did not settle for $url: ${t.message}" }
                } finally {
                    mainHandler.post {
                        if (!destroyed.get()) {
                            pageReady = true
                            runCatching { viewClient.onPageFinished(view, url) }
                            trace("load finished")
                        }
                    }
                }
            }, "playwright-webview-loadwait").apply { isDaemon = true }.start()
        }.onFailure { logger.debug(it) { "awaitLoad thread could not be started" } }
    }

    override fun loadUrl(url: String) = loadUrl(url, emptyMap())

    override fun postUrl(url: String, postData: ByteArray) {
        // POST body support is not part of the live-DOM surface; fall back to a plain navigation
        // so the page still loads rather than silently doing nothing.
        trace("postUrl(url=${url.take(160)}, postData=${postData.size}B) -- falling back to GET navigation")
        loadUrl(url)
    }

    override fun loadData(
        data: String,
        mimeType: String,
        encoding: String,
    ) = loadDataWithBaseURL(null, data, mimeType, encoding, null)

    override fun loadDataWithBaseURL(
        baseUrl: String?,
        data: String,
        mimeType: String?,
        encoding: String?,
        historyUrl: String?,
    ) {
        val origin = baseUrl ?: "about:blank"
        currentUrl = origin
        trace("loadDataWithBaseURL(base=${origin.take(120)}, mime=$mimeType, enc=$encoding, ${data.length} chars)")

        // ORDERING IS CRITICAL. The correct sequence is COMMIT -> onPageStarted -> load -> onPageFinished:
        //   - setDocument returns at commit (the served markup is parsed, but its deferred/module
        //     scripts have NOT run yet);
        //   - onPageStarted is then fired so the extension's document-start capture script is
        //     installed BEFORE the bundle's /api/v1/manga request;
        //   - awaitLoad waits for the `load` event;
        //   - onPageFinished fires last.
        //
        // It must NOT fire before the load task is submitted: the capture script calls
        // window[passPayloadName], which the page's inline bootstrap defines, so running it against
        // the blank/previous document throws and (worse) sets its `...CaptureInstalled` flag,
        // short-circuiting every later poll. And it must NOT fire after the load settles, because by
        // then the bundle has already fetched and the hook is too late (the bug being fixed).
        dispatchHostCall(
            onSuccess = { Unit },
            onFailure = { t -> logger.warn(t) { "loadDataWithBaseURL failed" } },
            call = { host ->
                val handle = ensureSession()
                if (handle == 0L) {
                    ""
                } else {
                    // Same as loadUrl: seed the host jar from the JVM store before the document load.
                    pushJvmCookiesToHost(host, origin)

                    // Install the window[name] shims before the load (present on the document the
                    // extension will query — the contract it relies on).
                    applyInterfaces()

                    // Serve the extension's markup as the response of a real navigation.
                    //
                    // NOTE: an engine-match branch that NAVIGATED FOR REAL when the solver's UA engine
                    // differed from the browser's was tried and REVERTED: Patchright cannot solve
                    // comix's Cloudflare managed/Turnstile challenge itself, so navigating to the real
                    // site just produced a challenge loop (chl_page/turnstile/pat) and a timeout. The
                    // route-served path is the one that works; the UA-bound-clearance limitation is a
                    // site/solver constraint, not something to paper over by forcing a navigation.
                    host.setDocument(handle, origin, data)
                    trace("loadDataWithBaseURL served document", "${data.length} chars")

                    // Fire onPageStarted at commit (document-start); its capture evaluates queue on
                    // this executor. Then queue the load-wait as a SEPARATE task, so those capture
                    // evaluates run during parse — before the document's module bundle.
                    notifyPageStarted(origin)
                    awaitLoadThenFinish(handle, origin)
                    origin
                }
            },
        )
    }

    override fun evaluateJavaScript(
        script: String,
        resultCallback: android.webkit.ValueCallback<String>?,
    ) {
        // NOTE: scripts evaluated from onPageStarted are deliberately NOT re-routed to
        // addInitScript, even though that looked like the faithful "document-start" semantics.
        //
        // comix-style runWebView builds its HTML with a bootstrap script INLINE in <head> that
        // defines window[passPayloadName] / window[bridgeName], and its capture script (evaluated
        // from onPageStarted) CALLS those functions. Context init scripts run BEFORE that inline
        // script, so routing the capture script there made it throw
        //     TypeError: window.<random> is not a function
        // and — worse — it had already set its `...CaptureInstalled` flag, so every later poll
        // short-circuited and the hook was never installed on the live page.
        //
        // The correct contract is: bootstrap runs inline during parse; the capture script runs from
        // onPageStarted/onPageFinished/poll AFTER it. Ordinary queued evaluates preserve that, so
        // that is what these are. Only the window[name] bridge shim belongs at document start.
        // NOTE: this fires on EVERY poll (the extension polls ~4x/second for the whole wait), so it
        // stays at DEBUG via trace(). Raising it to INFO floods the log for the entire timeout
        // window. Enable RENSAIO_RPC_VERBOSE=1 when diagnosing capture timing.
        trace(
            "evaluateJavaScript(bytes=${script.length}, callback=${resultCallback != null}, pageReady=$pageReady)",
            "script=${script.take(120)}",
        )
        dispatchHostCall(
            onSuccess = { result ->
                trace("evaluateJavaScript result", "len=${result.length}")
                runCatching { resultCallback?.onReceiveValue(result) }
            },
            onFailure = { t ->
                logger.warn(t) { "evaluateJavaScript failed" }
                runCatching { resultCallback?.onReceiveValue(null) }
            },
            call = { host ->
                val handle = ensureSession()
                if (handle == 0L) {
                    ""
                } else {
                    // Interfaces must exist before the evaluated script can reference them.
                    applyInterfaces()
                    host.evaluate(handle, script)
                }
            },
        )
    }

    override fun stopLoading() {
        // A host navigate/evaluate is short-lived and bounded by its own timeout; nothing to stop.
        trace("stopLoading (no-op)")
    }

    override fun reload() {
        val url = currentUrl
        if (url.isNotEmpty()) loadUrl(url) else trace("reload (no current URL, no-op)")
    }

    /**
     * Pushes the JVM cookie store's cookies for [url] into the host jar, so live-DOM sessions are
     * seeded with the SAME cookies the OkHttp chain uses — most importantly a solver-obtained
     * `cf_clearance`. The host decides precedence: Cloudflare cookies are seeded only when the
     * browser session does not already hold one (a browser-solved clearance is never clobbered).
     */
    private fun pushJvmCookiesToHost(host: extension.bridge.browser.IBrowserHost, url: String) {
        runCatching {
            val uri = URI(url)
            val manager = CookieHandlerBridge.ensureDefaultHandler()
            val cookies = manager.cookieStore.get(uri)
            // DIAGNOSTIC: does the store the push reads actually contain the solver's cf_clearance?
            logger.info {
                "JVM cookie push for $url: ${cookies.size} cookie(s) " +
                    "hasCf=${cookies.any { it.name.equals("cf_clearance", ignoreCase = true) }} " +
                    "names=${cookies.joinToString(",") { it.name }}"
            }
            if (cookies.isEmpty()) return
            val payload = hostJson.encodeToString(
                cookies.map { cookie ->
                    ProviderCookieDto(
                        name = cookie.name,
                        value = cookie.value,
                        domain = cookie.domain ?: uri.host ?: "",
                        path = cookie.path ?: "/",
                        secure = cookie.secure,
                        httpOnly = cookie.isHttpOnly,
                        expires = cookie.maxAge.takeIf { it > 0 }
                            ?.let { System.currentTimeMillis() / 1000.0 + it },
                    )
                },
            )
            host.setCookie(url, payload)
        }.onFailure { t ->
            logger.debug { "JVM cookie push to host failed (non-fatal): ${t.message}" }
        }
    }

    // ── JS interfaces ───────────────────────────────────────────────────────

    override fun addJavascriptInterface(obj: Any, interfaceName: String) {
        trace("addJavascriptInterface(name=$interfaceName, class=${obj.javaClass.name}, pageReady=$pageReady)")
        pendingInterfaces[interfaceName] = obj
        if (pageReady) {
            // Called from the main looper: hand off to the host executor rather than blocking here.
            applyInterfacesOffThread()
        }
    }

    override fun removeJavascriptInterface(interfaceName: String) {
        trace("removeJavascriptInterface(name=$interfaceName)")
        pendingInterfaces.remove(interfaceName)
        appliedInterfaces.remove(interfaceName)
    }

    /**
     * Applies every not-yet-registered interface to the live session.
     *
     * Runs INLINE: the only caller that reaches the host is [loadUrl]'s host-executor block, and
     * deferring to `hostExecutor.execute` from there would merely queue the work behind the current
     * task — after the page had already been reported ready.
     */
    private fun applyInterfaces() {
        pendingInterfaces.forEach { (name, obj) -> applyInterfaceNow(name, obj) }
    }

    /**
     * Applies the interfaces from a NON-host-executor thread (main-looper callbacks such as
     * [addJavascriptInterface] and [loadDataWithBaseURL]), where the blocking host calls must not
     * run inline.
     */
    private fun applyInterfacesOffThread() {
        if (pendingInterfaces.isEmpty() || hostExecutor.isShutdown) return
        runCatching {
            hostExecutor.execute { applyInterfaces() }
        }.onFailure { logger.debug(it) { "Interface application could not be queued" } }
    }

    private fun applyInterfaceNow(name: String, obj: Any) {
        if (!appliedInterfaces.add(name)) return
        val handle = sessionHandle
        if (handle == 0L) return

        try {
            // Register the target object with the host (C# keys it by handle+name) AND install the
            // window[name] shim. The shim is registered on the C# side as an INIT SCRIPT, not an
            // evaluate: evaluating it here raced the page's own navigation and produced
            //   "Execution context was destroyed, most likely because of a navigation"
            // after which window[name] was absent again. Init scripts are applied by the driver at
            // document start for every navigated document, which is the contract the extension JS
            // relies on (`window.android.onResponse(...)`).
            BrowserHostGateway.host().addBinding(handle, name, obj)
            logger.debug { "Applied JS interface '$name' (handle=$handle)" }
        } catch (t: Throwable) {
            appliedInterfaces.remove(name)
            logger.warn(t) { "Failed to bind interface '$name'" }
        }
    }

    // NOTE: the window[name] Proxy shim moved to the host side (BrowserHostBridge) where it is
    // registered as an INIT SCRIPT. Evaluating it from here raced the page's own navigation
    // ("Execution context was destroyed, most likely because of a navigation") and left
    // window[name] undefined on the document the extension actually queried.

    // ── Settings / clients ──────────────────────────────────────────────────

    override fun getSettings(): WebSettings = settings

    override fun setWebViewClient(client: WebViewClient) {
        viewClient = client
    }

    override fun getWebViewClient(): WebViewClient = viewClient

    override fun setWebChromeClient(client: WebChromeClient) {
        chromeClient = client
    }

    override fun getWebChromeClient(): WebChromeClient = chromeClient

    override fun setDownloadListener(listener: DownloadListener?) {
        downloadListener = listener
    }

    override fun getUrl(): String = currentUrl

    override fun getOriginalUrl(): String = currentUrl

    override fun getTitle(): String = ""

    override fun getProgress(): Int = if (pageReady) 100 else 0

    // ── Host dispatch ───────────────────────────────────────────────────────

    /**
     * Runs [call] on the dedicated host executor and posts [onSuccess]/[onFailure] back to the
     * main looper. Never blocks the caller (which is the emulated main looper).
     */
    private inline fun <T> dispatchHostCall(
        crossinline onSuccess: (T) -> Unit,
        crossinline onFailure: (Throwable) -> Unit,
        crossinline call: (extension.bridge.browser.IBrowserHost) -> T,
    ) {
        if (destroyed.get()) {
            logger.debug { "host call skipped: provider destroyed" }
            return
        }

        // Submission vs execution telemetry, bounded to the pre-ready window.
        //
        // This is what answers "how can an evaluate run before the browser is even launched?".
        // Every dispatch gets a monotonic sequence number; the QUEUE line is emitted where the call
        // is submitted and the EXEC line where it actually runs, both including the executing
        // THREAD. If EXEC lines arrive out of QUEUE order, or from more than one
        // `playwright-webview-host` thread, the executor is not serialising. If they arrive in
        // order from a single thread, then anything the extension saw earlier was simply submitted
        // earlier. Nothing about this is inferred.
        val seq = callSeq.incrementAndGet()
        if (!pageReady) {
            logger.debug {
                "[WebView] queue #$seq handle=$sessionHandle pageReady=false thread=${Thread.currentThread().name}"
            }
        }

        hostExecutor.execute {
            if (!pageReady) {
                logger.debug { "[WebView] exec #$seq thread=${Thread.currentThread().name}" }
            }
            try {
                val host = BrowserHostGateway.host()
                val result = call(host)
                mainHandler.post { if (!destroyed.get()) onSuccess(result) }
            } catch (t: Throwable) {
                mainHandler.post { if (!destroyed.get()) onFailure(t) }
            }
        }
    }

    /**
     * Uniform WebView-call telemetry, at DEBUG.
     *
     * Quiet by default: the JVM-side log floor (AndroidCompatLogForwarder) starts at INFO, so these
     * lines are dropped in the JVM unless the floor is explicitly lowered. To see them, enable the
     * RPC verbose experiment (RENSAIO_RPC_VERBOSE=1), which lowers both the .NET level and the JVM
     * floor. That keeps the per-call volume off in normal operation while remaining available for
     * diagnosing a webview-specific problem.
     */
    private fun trace(call: String, detail: String = "") {
        val suffix = if (detail.isEmpty()) "(handle=$sessionHandle)" else "$detail (handle=$sessionHandle)"
        logger.debug { "[WebView] $call $suffix" }
    }

    private fun jsonLiteral(value: String): String =
        buildString {
            append('"')
            value.forEach { c ->
                when (c) {
                    '\\' -> append("\\\\")
                    '"' -> append("\\\"")
                    '\n' -> append("\\n")
                    '\r' -> append("\\r")
                    '\t' -> append("\\t")
                    else -> append(c)
                }
            }
            append('"')
        }

    // ── Delegates ───────────────────────────────────────────────────────────

    override fun getViewDelegate(): ViewDelegate = viewDelegate

    override fun getScrollDelegate(): ScrollDelegate = scrollDelegate

    private inner class PlaywrightViewDelegate : ViewDelegate {
        override fun shouldDelayChildPressedState(): Boolean = false

        override fun onProvideVirtualStructure(structure: ViewStructure) {}

        override fun onProvideAutofillVirtualStructure(structure: ViewStructure, flags: Int) {}

        override fun autofill(values: SparseArray<AutofillValue>) {}

        override fun isVisibleToUserForAutofill(virtualId: Int): Boolean = true

        override fun onProvideContentCaptureStructure(structure: ViewStructure, flags: Int) {}

        override fun getAccessibilityNodeProvider(): AccessibilityNodeProvider? = null

        override fun onInitializeAccessibilityNodeInfo(info: AccessibilityNodeInfo) {}

        override fun onInitializeAccessibilityEvent(event: AccessibilityEvent) {}

        override fun performAccessibilityAction(action: Int, arguments: Bundle?): Boolean = false

        override fun setOverScrollMode(mode: Int) {}

        override fun setScrollBarStyle(style: Int) {}

        override fun onDrawVerticalScrollBar(
            canvas: Canvas,
            scrollBar: Drawable?,
            l: Int,
            t: Int,
            r: Int,
            b: Int,
        ) {}

        override fun onOverScrolled(scrollX: Int, scrollY: Int, clampedX: Boolean, clampedY: Boolean) {}

        override fun onWindowVisibilityChanged(visibility: Int) {}

        override fun onDraw(canvas: Canvas) {}

        override fun setLayoutParams(layoutParams: LayoutParams) {}

        override fun performLongClick(): Boolean = false

        override fun onConfigurationChanged(newConfig: Configuration) {}

        override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? = null

        override fun onDragEvent(event: DragEvent): Boolean = false

        override fun onKeyMultiple(keyCode: Int, repeatCount: Int, event: KeyEvent): Boolean = false

        override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean = false

        override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean = false

        override fun onAttachedToWindow() {}

        override fun onDetachedFromWindow() {}

        override fun onMovedToDisplay(displayId: Int, config: Configuration) {}

        override fun onVisibilityChanged(changedView: View, visibility: Int) {}

        override fun onWindowFocusChanged(hasWindowFocus: Boolean) {}

        override fun onFocusChanged(focused: Boolean, direction: Int, previouslyFocusedRect: Rect?) {}

        override fun setFrame(left: Int, top: Int, right: Int, bottom: Int): Boolean = true

        override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) {}

        override fun onScrollChanged(l: Int, t: Int, oldl: Int, oldt: Int) {}

        override fun dispatchKeyEvent(event: KeyEvent): Boolean = false

        override fun onTouchEvent(ev: MotionEvent): Boolean = false

        override fun onHoverEvent(event: MotionEvent): Boolean = false

        override fun onGenericMotionEvent(event: MotionEvent): Boolean = false

        override fun onTrackballEvent(ev: MotionEvent): Boolean = false

        override fun requestFocus(direction: Int, previouslyFocusedRect: Rect?): Boolean = true

        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {}

        override fun requestChildRectangleOnScreen(child: View, rect: Rect, immediate: Boolean): Boolean = false

        override fun setBackgroundColor(color: Int) {}

        override fun setLayerType(layerType: Int, paint: Paint?) {}

        override fun preDispatchDraw(canvas: Canvas) {}

        override fun onStartTemporaryDetach() {}

        override fun onFinishTemporaryDetach() {}

        override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {}

        override fun getHandler(originalHandler: Handler): Handler = originalHandler

        override fun findFocus(originalFocusedView: View): View = view

        override fun onApplyWindowInsets(insets: WindowInsets?): WindowInsets? = insets

        override fun onResolvePointerIcon(event: MotionEvent, pointerIndex: Int): PointerIcon? = null
    }

    private inner class PlaywrightScrollDelegate : ScrollDelegate {
        override fun computeHorizontalScrollRange(): Int = view.width.coerceAtLeast(0)

        override fun computeHorizontalScrollOffset(): Int = 0

        override fun computeVerticalScrollRange(): Int = view.height.coerceAtLeast(0)

        override fun computeVerticalScrollOffset(): Int = 0

        override fun computeVerticalScrollExtent(): Int = view.height.coerceAtLeast(0)

        override fun computeScroll() {}
    }

    // ── Unsupported WebViewProvider surface (safe no-ops) ───────────────────

    override fun setHorizontalScrollbarOverlay(overlay: Boolean) {}

    override fun setVerticalScrollbarOverlay(overlay: Boolean) {}

    override fun overlayHorizontalScrollbar(): Boolean = false

    override fun overlayVerticalScrollbar(): Boolean = false

    override fun getVisibleTitleHeight(): Int = 0

    override fun getCertificate(): SslCertificate? = null

    override fun setCertificate(certificate: SslCertificate?) {}

    override fun savePassword(host: String, username: String, password: String) {}

    override fun setHttpAuthUsernamePassword(host: String, realm: String, username: String, password: String) {}

    override fun getHttpAuthUsernamePassword(host: String, realm: String): Array<String>? = null

    override fun setNetworkAvailable(networkUp: Boolean) {}

    override fun saveState(outState: Bundle): WebBackForwardList? = null

    override fun savePicture(b: Bundle, dest: File): Boolean = false

    override fun restorePicture(b: Bundle, src: File): Boolean = false

    override fun restoreState(inState: Bundle): WebBackForwardList? = null

    override fun saveWebArchive(filename: String) {}

    override fun saveWebArchive(basename: String, autoname: Boolean, callback: android.webkit.ValueCallback<String>?) {}

    override fun canGoBack(): Boolean = false

    override fun goBack() {}

    override fun canGoForward(): Boolean = false

    override fun goForward() {}

    override fun canGoBackOrForward(steps: Int): Boolean = false

    override fun goBackOrForward(steps: Int) {}

    override fun isPrivateBrowsingEnabled(): Boolean = false

    override fun pageUp(top: Boolean): Boolean = false

    override fun pageDown(bottom: Boolean): Boolean = false

    override fun insertVisualStateCallback(requestId: Long, callback: VisualStateCallback?) {}

    override fun clearView() {}

    override fun capturePicture(): Picture? = null

    override fun createPrintDocumentAdapter(documentName: String?): PrintDocumentAdapter? = null

    override fun getScale(): Float = 1f

    override fun setInitialScale(scaleInPercent: Int) {}

    override fun invokeZoomPicker() {}

    override fun getHitTestResult(): HitTestResult? = null

    override fun requestFocusNodeHref(hrefMsg: Message?) {}

    override fun requestImageRef(msg: Message?) {}

    override fun getFavicon(): Bitmap? = null

    override fun getTouchIconUrl(): String? = null

    override fun getContentHeight(): Int = view.height.coerceAtLeast(0)

    override fun getContentWidth(): Int = view.width.coerceAtLeast(0)

    override fun pauseTimers() {}

    override fun resumeTimers() {}

    override fun onPause() {}

    override fun onResume() {}

    override fun isPaused(): Boolean = false

    override fun freeMemory() {}

    override fun clearCache(includeDiskFiles: Boolean) {}

    override fun clearFormData() {}

    override fun clearHistory() {}

    override fun clearSslPreferences() {}

    override fun copyBackForwardList(): WebBackForwardList? = null

    override fun setFindListener(listener: WebView.FindListener?) {}

    override fun findNext(forward: Boolean) {}

    override fun findAll(find: String): Int = 0

    override fun findAllAsync(find: String) {}

    override fun showFindDialog(text: String?, showIme: Boolean): Boolean = false

    override fun clearMatches() {}

    override fun documentHasImages(response: Message?) {}

    override fun getWebViewRenderProcess(): WebViewRenderProcess? = null

    override fun setWebViewRenderProcessClient(
        executor: java.util.concurrent.Executor?,
        client: WebViewRenderProcessClient?,
    ) {}

    override fun getWebViewRenderProcessClient(): WebViewRenderProcessClient? = null

    override fun setPictureListener(listener: PictureListener?) {}

    override fun createWebMessageChannel(): Array<WebMessagePort> = emptyArray()

    override fun postMessageToMainFrame(message: WebMessage?, targetOrigin: Uri?) {}

    override fun setMapTrackballToArrowKeys(setMap: Boolean) {}

    override fun flingScroll(vx: Int, vy: Int) {}

    override fun getZoomControls(): View? = null

    override fun canZoomIn(): Boolean = false

    override fun canZoomOut(): Boolean = false

    override fun zoomBy(zoomFactor: Float): Boolean = false

    override fun zoomIn(): Boolean = false

    override fun zoomOut(): Boolean = false

    override fun dumpViewHierarchyWithProperties(out: BufferedWriter?, level: Int) {}

    override fun findHierarchyView(className: String?, hashCode: Int): View? = null

    override fun setRendererPriorityPolicy(rendererRequestedPriority: Int, waivedWhenNotVisible: Boolean) {}

    override fun getRendererRequestedPriority(): Int = 0

    override fun getRendererPriorityWaivedWhenNotVisible(): Boolean = false

    override fun setTextClassifier(textClassifier: TextClassifier?) {}

    override fun getTextClassifier(): TextClassifier = TextClassifier.NO_OP

    override fun notifyFindDialogDismissed() {}
}

/** Wire DTO mirroring the host's BrowserCookie, used only for the JVM->host cookie push. */
@Serializable
private data class ProviderCookieDto(
    val name: String,
    val value: String,
    val domain: String = "",
    val path: String = "/",
    val secure: Boolean = false,
    val httpOnly: Boolean = false,
    val expires: Double? = null,
)

private val hostJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }
