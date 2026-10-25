package eu.kanade.tachiyomi.network.interceptor

import android.annotation.SuppressLint
import android.util.Base64
import android.util.Log
import android.webkit.CookieHandlerBridge
import extension.bridge.Settings
import extension.bridge.browser.BrowserHostGateway
import eu.kanade.tachiyomi.network.sourceMetadata
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.Headers.Companion.toHeaders
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import java.io.IOException
import java.net.HttpCookie
import java.net.URI

/**
 * An OkHttp interceptor that executes HTTP requests through a WebView using JavaScript `fetch` API.
 *
 * This interceptor is useful for bypassing certain protections (like Cloudflare Turnstile) or when you need
 * to execute requests in a browser context. It works by:
 * 1. Optionally loading a URL in the WebView to establish a specific domain context
 * 2. Executing a JavaScript `fetch` with the original request details via `evaluateJavascript`
 * 3. Encoding the response in base64 and sending it back via a JavaScript interface
 * 4. Building an OkHttp Response from the WebView response
 *
 * **Best Practices:**
 * - In most cases, you don't need to specify `loadUrl` - the interceptor will work without it
 * - Only use `loadUrl` in special cases when you need a specific URL/domain context for the fetch
 * - Always use the `filter` parameter to only intercept requests from the same domain to avoid CORS issues
 * - The same-domain context allows JavaScript execution and avoids Cross-Origin Resource Sharing problems
 *
 * **Resource bounds (since the jcef_helper leak fix):**
 * - The authoritative renderer budget is [RendererGate], enforced inside
 *   [xyz.nulldev.androidcompat.webkit.KcefWebViewProvider] at browser creation — covering
 *   interceptor, pool, AND extension-owned WebViews (e.g. Keiyoushi-style `runWebView`).
 * - This interceptor consults [RendererGate.canSpawn] before posting work to the looper; when the
 *   budget is exhausted it degrades to the direct OkHttp chain instead of spawning another helper.
 * - WebViews are pooled per host ([WebViewPool]); a pooled WebView's JS interface is registered
 *   ONCE (a per-entry dispatcher) and the per-request handler is swapped via an atomic reference,
 *   so consecutive requests on a reused WebView never race add/remove ordering.
 * - Destruction/eviction always happens in a `finally` block on success/timeout/error, so no
 *   renderer process outlives its request. The old `postDelayed(destroy, 1000)` race is gone.
 *
 * @param filter Optional function that determines which requests should be intercepted.
 *   Returns `true` to intercept the request via WebView, `false` to proceed normally.
 *   If `null`, all requests are intercepted.
 *   **Recommended**: Filter by domain to only intercept requests from the same domain:
 *   ```kotlin
 *   filter = { request -> request.url.toString().startsWith(baseUrl) }
 *   ```
 *
 * @param timeout Timeout in seconds for waiting for the WebView response. Default is 60 seconds.
 *
 * @param loadUrl Optional URL to load in the WebView to establish a specific domain context before executing the fetch.
 *   **Only use in special cases** when you need a specific URL/domain context. In most cases, this can be `null`.
 *   If provided, use a lightweight file from the same `baseUrl` domain:
 *   - `/robots.txt` (recommended - very lightweight)
 *   - `/favicon.ico` (small image file)
 *   - A small CSS file
 *   This ensures fast loading, same domain context (avoiding CORS), and proper JavaScript execution.
 *
 * @sample
 * ```kotlin
 * // Basic usage without loadUrl
 * override val client = network.client.newBuilder()
 *     .addInterceptor(
 *         WebViewFetchInterceptor(
 *             filter = { request -> request.url.toString().startsWith(baseUrl) }
 *         )
 *     )
 *     .build()
 *
 * // Special case: with loadUrl for specific domain context
 * override val client = network.client.newBuilder()
 *     .addInterceptor(
 *         WebViewFetchInterceptor(
 *             filter = { request -> request.url.toString().startsWith(baseUrl) },
 *             loadUrl = "$baseUrl/robots.txt"
 *         )
 *     )
 *     .build()
 * ```
 */
class WebViewFetchInterceptor(
    private val filter: ((Request) -> Boolean)? = null,
    private val timeout: Long = 60,
    private val loadUrl: String? = null,
) : Interceptor {

    private val interceptorName: String = this::class.simpleName ?: "WebViewFetchInterceptor"

    // NOTE: the WebViewPool/Dispatcher/JsInterface machinery was removed with JCEF. The interceptor
    // now delegates Tier-1 fetches to BrowserHostGateway and degrades to the direct OkHttp chain.

    /**
     * Intercepts the HTTP request and either processes it through WebView or proceeds normally.
     *
     * If the filter function returns `false` or the request doesn't match the filter criteria,
     * the request proceeds normally through the OkHttp chain. Otherwise, it's executed via WebView.
     *
     * @param chain The OkHttp interceptor chain
     * @return The HTTP response, either from WebView or from the normal chain
     * @throws IOException If the WebView request times out or encounters an error
     */
    @Synchronized
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()

        val metadata = request.sourceMetadata()
        val enabledForPackage = metadata?.let { meta ->
            val overrides = Settings.interceptorOverrides
            overrides[meta.packageName]?.get(interceptorName) ?: false
        } ?: false

        if (!enabledForPackage) {
            return chain.proceed(request)
        }

        // Use filter function if provided
        val shouldIntercept = filter?.invoke(request) ?: true

        if (!shouldIntercept) {
            return chain.proceed(request)
        }

        Log.d("WebViewFetchInterceptor", "Intercepting request: ${request.url}")

        return proceedWithWebView(chain, request)
    }

    /**
     * Executes the HTTP request through a WebView using JavaScript fetch API.
     *
     * This method:
     * 1. Prepares the request data (URL, method, headers, body)
     * 2. Consults the global renderer budget and acquires a pooled WebView (keyed by host)
     * 3. Establishes context:
     *    - If `loadUrl` is provided, loads that URL
     *    - Otherwise, uses the request's domain as base URL with empty HTML content
     * 4. Executes a JavaScript `fetch` with the original request details
     * 5. Waits for the response (with configurable timeout)
     * 6. Decodes the base64-encoded response and builds an OkHttp Response
     * 7. Returns the WebView to the pool (success) or evicts it (timeout/error) in a `finally`
     *    block — no renderer process is ever leaked.
     *
     * @param chain The OkHttp interceptor chain (used for graceful degradation)
     * @param request The original OkHttp request to execute
     * @return An OkHttp Response built from the WebView fetch response
     * @throws IOException If the request times out or the WebView returns an error
     */
    @SuppressLint("SetJavaScriptEnabled", "AddJavascriptInterface")
    private fun proceedWithWebView(chain: Interceptor.Chain, request: Request): Response {
        // Tier-1 fast path: when a .NET browser host is installed (Patchright/Playwright) and has
        // capacity, execute the fetch through it. JCEF has been removed, so when the host is
        // unavailable or fails the only remaining fallback is the direct OkHttp chain.
        if (BrowserHostGateway.capacityAvailable()) {
            val hostResponse = tryBrowserHost(request)
            if (hostResponse != null) {
                return hostResponse
            }
        }

        Log.w(
            "WebViewFetchInterceptor",
            "Browser host unavailable or failed for ${request.url}; falling back to direct network",
        )
        return chain.proceed(request)
    }

private fun String.asJsonLiteral(): String = Json.encodeToString(this)

private fun Map<String, String>.asJsonLiteral(): String = Json.encodeToString(this)

/**
 * Wire DTOs for the single JSON hop across the IKVM boundary (BrowserRequest <-> BrowserResponse).
 * These mirror the .NET contracts in Mihon.ExtensionsBridge.Models/Abstractions/Browser and are
 * serialized with the same camelCase property names.
 */
@Serializable
internal data class BrowserHostRequest(
    val url: String,
    val method: String = "GET",
    val headers: Map<String, String> = emptyMap(),
    val body: String? = null,
    val contextUrl: String? = null,
    val timeoutSeconds: Int = 60,
    val waitUntil: String? = null,
    val cookies: List<BrowserHostCookie> = emptyList(),
)

/** Engine-neutral cookie, mirroring the .NET BrowserCookie contract. */
@Serializable
internal data class BrowserHostCookie(
    val name: String,
    val value: String,
    val domain: String = "",
    val path: String = "/",
    val secure: Boolean = false,
    val httpOnly: Boolean = false,
    val expires: Double? = null,
)

@Serializable
internal data class BrowserHostResponse(
    val statusCode: Int = 0,
    val statusMessage: String = "",
    val headers: Map<String, String> = emptyMap(),
    val bodyBase64: String = "",
    val cookies: List<BrowserHostCookie> = emptyList(),
    val error: String? = null,
)

private val hostJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }

/**
 * Tier-1 execution through the .NET browser host. Returns `null` (so the caller falls back to
 * JCEF / the direct chain) when the host is not installed, errors, or returns an error payload.
 *
 * Cookie unification (plan §12.2): the JVM cookie store stays the source of truth. Its cookies
 * are seeded into the browser before the fetch, and the browser's cookies (notably a freshly
 * obtained `cf_clearance`) are merged back afterwards so subsequent OkHttp calls carry them.
 */
private fun tryBrowserHost(request: Request): Response? {
    return try {
        val requestHeaders = request.headers.toMultimap()
            .mapValues { it.value.lastOrNull() ?: "" }
            .toMutableMap()
        val contentType = request.body?.contentType()?.toString()
        if (!contentType.isNullOrEmpty()) {
            requestHeaders["Content-Type"] = contentType
        }
        val bodyString = request.body?.let { body ->
            val buffer = Buffer()
            body.writeTo(buffer)
            buffer.readUtf8()
        }

        val payload = hostJson.encodeToString(
            BrowserHostRequest(
                url = request.url.toString(),
                method = request.method,
                headers = requestHeaders,
                body = bodyString,
                contextUrl = "${request.url.scheme}://${request.url.host}/",
                cookies = readJvmCookies(request.url.toString()),
            ),
        )

        val raw = BrowserHostGateway.host().execute(payload)
        val parsed = hostJson.decodeFromString<BrowserHostResponse>(raw)

        if (!parsed.error.isNullOrEmpty()) {
            Log.w("WebViewFetchInterceptor", "Browser host error: ${parsed.error}; falling back")
            return null
        }

        // Merge the browser's cookies back into the JVM store (source of truth).
        writeJvmCookies(request.url.toString(), parsed.cookies)

        val bodyBytes = if (parsed.bodyBase64.isNotEmpty()) {
            Base64.decode(parsed.bodyBase64, Base64.NO_WRAP)
        } else {
            ByteArray(0)
        }

        val responseHeaders = parsed.headers.toHeaders()
        val mediaType = responseHeaders["Content-Type"]?.toMediaType()
            ?: "application/octet-stream".toMediaType()

        Response.Builder()
            .request(request)
            .protocol(Protocol.HTTP_1_1)
            .code(parsed.statusCode)
            .message(parsed.statusMessage)
            .headers(responseHeaders)
            .body(bodyBytes.toResponseBody(mediaType))
            .build()
    } catch (t: Throwable) {
        Log.w("WebViewFetchInterceptor", "Browser host execution failed: ${t.message}; falling back", t)
        null
    }
}

/**
 * Reads the JVM cookie store for [url] and converts it to the host's cookie DTOs. The JVM store
 * remains the source of truth (plan §12.2), so this is the seeding direction.
 */
private fun readJvmCookies(url: String): List<BrowserHostCookie> {
    return runCatching {
        val uri = URI(url)
        val manager = CookieHandlerBridge.ensureDefaultHandler()
        manager.cookieStore.get(uri).map { cookie ->
            BrowserHostCookie(
                name = cookie.name,
                value = cookie.value,
                domain = cookie.domain ?: uri.host ?: "",
                path = cookie.path ?: "/",
                secure = cookie.secure,
                httpOnly = cookie.isHttpOnly,
                expires = cookie.maxAge.takeIf { it > 0 }?.let { System.currentTimeMillis() / 1000.0 + it },
            )
        }
    }.getOrElse { t ->
        Log.w("WebViewFetchInterceptor", "Failed to read JVM cookies for $url: ${t.message}")
        emptyList()
    }
}

/**
 * Merges browser-observed cookies (e.g. a solved `cf_clearance`) into the JVM cookie store so
 * subsequent OkHttp requests reuse the cleared session.
 */
private fun writeJvmCookies(url: String, cookies: List<BrowserHostCookie>) {
    if (cookies.isEmpty()) {
        return
    }

    runCatching {
        val uri = URI(url)
        val manager = CookieHandlerBridge.ensureDefaultHandler()
        for (cookie in cookies) {
            val httpCookie = HttpCookie(cookie.name, cookie.value).apply {
                domain = cookie.domain.ifBlank { uri.host ?: "" }
                path = cookie.path.ifBlank { "/" }
                this.secure = cookie.secure
                isHttpOnly = cookie.httpOnly
                cookie.expires?.let { expiry ->
                    val secondsLeft = (expiry - System.currentTimeMillis() / 1000.0).toLong()
                    if (secondsLeft > 0) {
                        maxAge = secondsLeft
                    }
                }
            }
            manager.cookieStore.add(uri, httpCookie)
        }
        Log.d("WebViewFetchInterceptor", "Merged ${cookies.size} browser cookies into the JVM store for $url")
    }.onFailure { t ->
        Log.w("WebViewFetchInterceptor", "Failed to merge browser cookies for $url: ${t.message}")
    }
}
}
