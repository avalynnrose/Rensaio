package extension.bridge.network

import extension.bridge.ProxySettings
import extension.bridge.logging.androidCompatLogger
import extension.bridge.security.ProxyTrustInstaller
import java.net.Authenticator
import java.net.InetSocketAddress
import java.net.PasswordAuthentication
import java.net.Proxy
import java.net.ProxySelector
import java.net.SocketAddress
import java.net.URI
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * Applies the configured proxy to the JVM network stack.
 *
 * TWO mechanisms are supported, chosen by [ProxySettings.proxyScheme]:
 *
 *  - `"http"` — used by a transparent/TLS-terminating proxy such as trawl. Installs a
 *    [ProxySelector] because **OkHttp reads `ProxySelector.getDefault()`**, NOT the
 *    `http(s).proxyHost`/`socksProxyHost` system properties. Without the selector the
 *    extension HTTP client silently bypassed the proxy even though it was configured.
 *  - `"socks5"` — the historical behaviour: `socksProxyHost`/`socksProxyPort`/`socksProxyVersion`
 *    system properties plus an [Authenticator].
 *
 * A host/port cannot serve both schemes at once, so this is a switch rather than an addition.
 *
 * When a CA is configured ([ProxySettings.caCertPath]) the proxy is TLS-terminating, so
 * [ProxyTrustInstaller] is also invoked to trust its certificates.
 */
object SystemProxyBridge {
    private const val SIGNATURE_KEY = "extension.bridge.socksProxy.signature"
    private val lock = ReentrantLock()
    private val logger = androidCompatLogger(SystemProxyBridge::class.java)

    fun apply(settings: ProxySettings) {
        lock.withLock {
            val signature = signature(settings)
            val currentSignature = System.getProperty(SIGNATURE_KEY)
            if (currentSignature == signature) {
                return
            }

            if (!settings.proxyEnabled) {
                clearProxy()
                return
            }

            when (settings.proxyScheme.lowercase()) {
                "http" -> applyHttpProxy(settings)
                "socks4" -> applySocksProxy(settings, version = 4)
                // Default (and "socks5"): SOCKS5, the historical behaviour.
                else -> applySocksProxy(settings, version = 5)
            }

            ProxyTrustInstaller.install(settings.caCertPath, settings.ignoreTlsErrors)

            System.setProperty(SIGNATURE_KEY, signature)
        }
    }

    /**
     * HTTP proxy: install a default [ProxySelector]. Every host is routed to the proxy; the
     * proxy itself is plain HTTP (including for HTTPS CONNECT), which is why [Proxy.Type.HTTP]
     * — not SOCKS — is used.
     */
    private fun applyHttpProxy(settings: ProxySettings) {
        val port = settings.proxyPort.toIntOrNull() ?: 0
        if (port <= 0) {
            logger.warn { "HTTP proxy enabled but port '${settings.proxyPort}' is invalid; not applying." }
            return
        }

        // Clear any previously-installed SOCKS system properties so the two mechanisms cannot
        // both be active (the switch is exclusive).
        clearSocksSystemProperties()

        val address = InetSocketAddress(normalizeHost(settings.proxyHost), port)
        ProxySelector.setDefault(
            object : ProxySelector() {
                override fun select(uri: URI?): List<Proxy> = listOf(Proxy(Proxy.Type.HTTP, address))

                override fun connectFailed(uri: URI?, sa: SocketAddress?, ioe: java.io.IOException?) {
                    logger.warn { "HTTP proxy connection failed for $uri: ${ioe?.message}" }
                }
            },
        )

        installAuthenticator(settings)
        logger.info { "Applied HTTP proxy configuration: ${normalizeHost(settings.proxyHost)}:$port" }
    }

    /** Historical SOCKS behaviour: JVM system properties + an authenticator. */
    private fun applySocksProxy(settings: ProxySettings, version: Int) {
        // Remove a previously-installed HTTP selector so it cannot shadow the SOCKS properties.
        ProxySelector.setDefault(null)

        val host = normalizeHost(settings.proxyHost)
        System.setProperty("socksProxyHost", host)
        System.setProperty("socksProxyPort", settings.proxyPort)
        System.setProperty("socksProxyVersion", version.toString())

        installAuthenticator(settings)
        logger.info { "Applied SOCKS$version proxy configuration: $host:${settings.proxyPort}" }
    }

    /**
     * Accepts both a bare host and a full URL, as the UI allows either:
     * `http://192.168.1.5`, `socks5://192.168.1.5`, `192.168.1.5:8192`, `[::1]`.
     * Mirrors `ProxyEndpoint.NormalizeHost` on the C# side so both stacks agree.
     */
    private fun normalizeHost(raw: String): String {
        var value = raw.trim()
        if (value.isEmpty()) return value

        val schemeSep = value.indexOf("://")
        if (schemeSep >= 0) value = value.substring(schemeSep + 3)

        val pathStart = value.indexOfFirst { it == '/' || it == '?' || it == '#' }
        if (pathStart >= 0) value = value.substring(0, pathStart)

        val at = value.lastIndexOf('@')
        if (at >= 0) value = value.substring(at + 1)

        if (value.startsWith("[")) {
            // Bracketed IPv6: keep through the closing bracket ("[::1]:1080" -> "[::1]").
            val close = value.indexOf(']')
            if (close >= 0) value = value.substring(0, close + 1)
        } else {
            val colon = value.lastIndexOf(':')
            if (colon > 0 && value.indexOf(':') == colon) value = value.substring(0, colon)
        }

        return value.trim()
    }

    private fun installAuthenticator(settings: ProxySettings) {
        val authenticator =
            if (settings.proxyUsername.isNotBlank() || settings.proxyPassword.isNotBlank()) {
                object : Authenticator() {
                    override fun getPasswordAuthentication(): PasswordAuthentication? {
                        val protocol = requestingProtocol ?: return null
                        return if (
                            protocol.startsWith("SOCKS", ignoreCase = true) ||
                            protocol.startsWith("HTTP", ignoreCase = true)
                        ) {
                            PasswordAuthentication(settings.proxyUsername, settings.proxyPassword.toCharArray())
                        } else {
                            null
                        }
                    }
                }
            } else {
                null
            }
        Authenticator.setDefault(authenticator)
    }

    private fun clearProxy() {
        val currentSignature = System.getProperty(SIGNATURE_KEY) ?: return
        clearSocksSystemProperties()
        ProxySelector.setDefault(null)
        Authenticator.setDefault(null)
        ProxyTrustInstaller.install("", false)
        System.clearProperty(SIGNATURE_KEY)
        logger.info { "Cleared proxy configuration (signature=$currentSignature)" }
    }

    private fun clearSocksSystemProperties() {
        System.clearProperty("socksProxyHost")
        System.clearProperty("socksProxyPort")
        System.clearProperty("socksProxyVersion")
    }

    private fun signature(settings: ProxySettings): String {
        if (!settings.proxyEnabled) return "disabled"
        return buildString {
            append(settings.proxyEnabled)
            append('|').append(settings.proxyScheme)
            append('|').append(settings.proxyHost)
            append('|').append(settings.proxyPort)
            append('|').append(settings.proxyUsername)
            append('|').append(settings.proxyPassword.hashCode())
            append('|').append(settings.caCertPath)
            append('|').append(settings.ignoreTlsErrors)
        }
    }
}
