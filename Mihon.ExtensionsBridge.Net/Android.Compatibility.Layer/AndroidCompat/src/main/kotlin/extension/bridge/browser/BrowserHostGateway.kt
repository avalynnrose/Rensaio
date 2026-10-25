package extension.bridge.browser

import extension.bridge.logging.androidCompatLogger

/**
 * Thin client contract for the .NET-hosted browser. The implementation lives in the C# host
 * (real CoreCLR) and is installed into [BrowserHostGateway] at startup — the inverse of the
 * existing CefPumpBridge direction.
 *
 * Only JSON strings cross the IKVM boundary; pooling, retries, cookie jars and provider
 * selection all stay in plain .NET.
 *
 * ## Tier 1 (solver fast path)
 * [execute] performs one fetch inside a browser context.
 *
 * ## Tier 2 (live DOM, used by [PlaywrightWebViewProvider])
 * [create]/[destroy] own a long-lived session; [navigate]/[evaluate] drive it; [addBinding]
 * exposes a JVM object to the page. The JS→JVM direction is deliberately avoided: the page
 * calls into .NET, and .NET invokes the JVM object (C#→Java, which is the proven-safe direction).
 *
 * Threading rule (critical): [execute] must never be called on the Android main looper. The
 * solver path runs on the OkHttp interceptor thread (safe to block); the live-DOM path reuses
 * the existing looper + CountDownLatch pattern.
 */
interface IBrowserHost {
    // ── Tier 1 ──────────────────────────────────────────────────────────────
    /** Executes a BrowserRequest (JSON) and returns a BrowserResponse (JSON). */
    fun execute(requestJson: String): String

    fun getCookie(url: String): String

    fun setCookie(url: String, value: String)

    fun isAvailable(): Boolean

    /** Replaces the old RendererGate.canSpawn() advisory check. */
    fun capacityAvailable(): Boolean

    // ── Tier 2 (live DOM) ───────────────────────────────────────────────────
    /** Creates a long-lived session and returns its handle. */
    fun create(): Long

    /** Destroys a session created by [create]. Safe to call more than once. */
    fun destroy(handle: Long)

    /**
     * Navigates the session's page to [url] and returns when the navigation reaches COMMIT.
     *
     * Commit (not load) is the correct point: a real `WebView` fires `onPageStarted` at commit,
     * *before* the document's own deferred/module scripts run, so a document-start script the
     * extension evaluates from `onPageStarted` is installed in time to observe the page's first
     * requests. Callers must invoke [awaitLoad] before firing `onPageFinished`.
     */
    fun navigate(handle: Long, url: String): String

    /**
     * Blocks until the page started by the most recent [navigate]/[setDocument] has reached its
     * `load` event (i.e. `onPageFinished` time), then returns.
     *
     * This exists to reproduce the real WebView ordering: [navigate]/[setDocument] return at
     * navigation COMMIT (so the caller can fire `onPageStarted` while the document is still
     * parsing), and the caller then calls `awaitLoad()` before firing `onPageFinished`. Without the
     * split, a document-start script evaluated from `onPageStarted` would either run against the
     * blank/previous document (fired before the load) or after the page's own deferred/module
     * scripts had already run (fired after) — the latter being the bug this ordering fixes.
     */
    fun awaitLoad(handle: Long)

    /** Evaluates [script] in the session's page and returns the result. */
    fun evaluate(handle: Long, script: String): String

    /**
     * Registers [script] to run at DOCUMENT START of every navigation.
     *
     * This is the faithful semantics of `WebViewClient.onPageStarted`: a real WebView fires it
     * before the document's own scripts execute, so a script that monkey-patches `fetch`/`XHR`/
     * `JSON.parse` is installed in time to observe the page's first requests. Evaluating it after
     * the load (as we did) is always too late — the page's API calls have already gone out.
     */
    fun addInitScript(handle: Long, script: String)

    /**
     * Loads [html] as the document at [url] (the `loadDataWithBaseURL` path), establishing that URL
     * as the origin.
     *
     * Implementations must deliver the markup as the result of a real navigation rather than via
     * `document.write`: writing into the existing document breaks the node references held by the
     * page's framework (React throws "Failed to execute 'removeChild' …") and does not run
     * JS-interface init scripts, so the `window[name]` bridges disappear.
     */
    fun setDocument(handle: Long, url: String, html: String)

    /**
     * Exposes [target] to the page as `window[name]`. Method calls from JS are routed to the
     * matching JVM method on [target] via C#→Java reflection.
     */
    fun addBinding(handle: Long, name: String, target: Any)

    /** Captures a PNG screenshot of the session's page. */
    fun screenshot(handle: Long): ByteArray

    /** Synchronises the JVM cookie store into the session. */
    fun syncCookies(url: String, cookiesJson: String)
}

/**
 * Settable slot for the .NET browser host.
 *
 * [setHost] accepts `Any` so the C# side can install its implementation without the host
 * assembly needing a compile-time reference to this interface (it is installed through
 * java.lang.reflect.Proxy). The cast is performed here, inside the JVM.
 */
object BrowserHostGateway {
    private val logger = androidCompatLogger(BrowserHostGateway::class.java)

    @Volatile
    private var host: IBrowserHost? = null

    fun setHost(candidate: Any?) {
        if (candidate == null) {
            host = null
            logger.warn { "Browser host cleared (null candidate)" }
            return
        }

        // Logged at WARN so it cannot be filtered out by a configured minimum level: the C# side
        // reports installation success independently, so without this the two halves can disagree
        // silently (which is exactly what happened: C# said "installed", Kotlin saw nothing).
        logger.warn {
            "Browser host candidate received: ${candidate.javaClass.name} " +
                "implementsIBrowserHost=${candidate is IBrowserHost}"
        }

        val typed = candidate as? IBrowserHost
        if (typed == null) {
            logger.warn { "Browser host candidate does not implement IBrowserHost: ${candidate.javaClass.name}" }
            return
        }

        host = typed

        // Report the probe outcome AND any exception, rather than a bare boolean.
        val outcome = runCatching { typed.isAvailable() }
        logger.warn {
            if (outcome.isSuccess) {
                "Browser host installed: available=${outcome.getOrThrow()}"
            } else {
                "Browser host installed but isAvailable() threw: ${outcome.exceptionOrNull()}"
            }
        }
    }

    fun clearHost() {
        host = null
    }

    fun isInstalled(): Boolean = host != null

    fun host(): IBrowserHost = host ?: error("Browser host not installed")

    /**
     * Non-throwing availability probe used by the interceptor's fast advisory check and the
     * WebView provider factory. Never throws, but [isolate] reports the FIRST exception so a
     * failing probe is diagnosable instead of silently reading as "not available".
     */
    fun isAvailable(): Boolean {
        val current = host
        if (current == null) {
            logMissingHostOnce()
            return false
        }
        return probe(probeName = "isAvailable") { current.isAvailable() }
    }

    /** Non-throwing capacity probe (replaces RendererGate.canSpawn()). */
    fun capacityAvailable(): Boolean {
        val current = host
        if (current == null) {
            logMissingHostOnce()
            return false
        }
        return probe(probeName = "capacityAvailable") { current.capacityAvailable() }
    }

    private inline fun probe(probeName: String, call: () -> Boolean): Boolean =
        runCatching { call() }.getOrElse { t ->
            if (probeFailureLogged.compareAndSet(false, true)) {
                logger.warn { "Browser host $probeName() threw; treating as unavailable: $t" }
            }
            false
        }

    /** Reports the missing-host case once, to avoid flooding the log per WebView. */
    private fun logMissingHostOnce() {
        if (missingHostLogged.compareAndSet(false, true)) {
            logger.warn {
                "Browser host is null in the gateway: C# never installed into this JVM's " +
                    "BrowserHostGateway (or installed into a different class loader)."
            }
        }
    }

    private val missingHostLogged = java.util.concurrent.atomic.AtomicBoolean(false)
    private val probeFailureLogged = java.util.concurrent.atomic.AtomicBoolean(false)

    /** Non-throwing install check used by the WebView provider factory selection. */
    fun isUsable(): Boolean = isAvailable()
}
