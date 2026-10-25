package xyz.nulldev.androidcompat.webkit

import android.util.Log
import android.webkit.CookieHandlerBridge
import android.webkit.CookieManager
import android.webkit.ValueCallback
import android.webkit.WebView
import extension.bridge.browser.BrowserHostGateway
import extension.bridge.logging.androidCompatLogger
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.net.HttpCookie
import java.net.URI

/**
 * Engine-neutral cookie DTO mirroring the .NET `BrowserCookie` contract.
 * Declared here (not shared with the interceptor's copy) to keep this class free of any
 * dependency on the network package.
 */
@Serializable
private data class BrowserCookieDto(
    val name: String,
    val value: String,
    val domain: String = "",
    val path: String = "/",
    val secure: Boolean = false,
    val httpOnly: Boolean = false,
    val expires: Double? = null,
)

private val cookieJson = Json { ignoreUnknownKeys = true; encodeDefaults = true }

@Suppress("DEPRECATION")
class CookieManagerImpl : CookieManager() {
    private val logger = androidCompatLogger(CookieManagerImpl::class.java)

    private val cookieHandler by lazy(LazyThreadSafetyMode.NONE) {
        CookieHandlerBridge.ensureDefaultHandler()
    }
    private var acceptCookie = true
    private var acceptThirdPartyCookies = true
    private var allowFileSchemeCookies = false

    override fun setAcceptCookie(accept: Boolean) {
        acceptCookie = accept
    }

    override fun acceptCookie(): Boolean = acceptCookie

    override fun setAcceptThirdPartyCookies(
        webview: WebView?,
        accept: Boolean,
    ) {
        acceptThirdPartyCookies = accept
    }

    override fun acceptThirdPartyCookies(webview: WebView?): Boolean = acceptThirdPartyCookies

    override fun setCookie(
        url: String,
        value: String?,
    ) {
        val uri =
            if (url.startsWith("http")) {
                URI(url)
            } else {
                URI("http://$url")
            }

        val parsed = HttpCookie.parse(value)
        parsed.forEach {
            cookieHandler.cookieStore.add(uri, it)
        }

        // Write-through to the .NET browser host so a WebView-driven request on the same host
        // sees the same cookies. The JVM store stays authoritative (plan §12.2); a failure here
        // is non-fatal because the interceptor also seeds cookies from the store per fetch.
        pushToBrowserHost(url, uri, parsed)
    }

    override fun setCookie(
        url: String,
        value: String?,
        callback: ValueCallback<Boolean>?,
    ) {
        setCookie(url, value)
        callback?.onReceiveValue(true)
    }

    override fun getCookie(url: String): String {
        val uri =
            if (url.startsWith("http")) {
                URI(url)
            } else {
                URI("http://$url")
            }
        return cookieHandler.cookieStore
            .get(uri)
            .joinToString("; ") { "${it.name}=${it.value}" }
    }

    @Deprecated("Deprecated in Java")
    override fun removeSessionCookie() {}

    override fun removeSessionCookies(callback: ValueCallback<Boolean>?) {}

    @Deprecated("Deprecated in Java")
    override fun removeExpiredCookie() {}

    @Deprecated("Deprecated in Java")
    override fun removeAllCookie() {
        cookieHandler.cookieStore.removeAll()
    }

    override fun removeAllCookies(callback: ValueCallback<Boolean>?) {
        val removedCookies = cookieHandler.cookieStore.removeAll()
        callback?.onReceiveValue(removedCookies)
    }

    override fun hasCookies(): Boolean = cookieHandler.cookieStore.cookies.isNotEmpty()

    override fun flush() {}

    override fun allowFileSchemeCookiesImpl(): Boolean = allowFileSchemeCookies

    override fun setAcceptFileSchemeCookiesImpl(accept: Boolean) {
        allowFileSchemeCookies = accept
    }

    /**
     * Pushes freshly stored cookies to the .NET browser host so WebView-driven requests on the
     * same host carry them. Never throws: the JVM store is authoritative and the interceptor
     * re-seeds from it per fetch, so a missing/unavailable host must not break cookie writes.
     */
    private fun pushToBrowserHost(
        url: String,
        uri: URI,
        cookies: List<HttpCookie>,
    ) {
        if (cookies.isEmpty() || !BrowserHostGateway.isInstalled()) {
            return
        }

        runCatching {
            val payload = cookieJson.encodeToString(
                cookies.map { cookie ->
                    BrowserCookieDto(
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
            BrowserHostGateway.host().setCookie(url, payload)
        }.onFailure { t ->
            Log.w(TAG, "Browser host cookie push failed (JVM store retains the cookie): ${t.message}")
        }
    }

    private companion object {
        const val TAG = "CookieManagerImpl"
    }
}
