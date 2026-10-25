package extension.bridge.security

import extension.bridge.logging.androidCompatLogger
import java.io.File
import java.net.HttpURLConnection
import java.net.Proxy
import java.net.URL
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.concurrent.locks.ReentrantLock
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.withLock

/**
 * Trusts a TLS-terminating proxy's root CA (trawl) across the JVM.
 *
 * WHY A SINGLE LIVE MANAGER:
 * A transparent proxy re-signs every connection with a per-host leaf, so the CA — not the leaf —
 * is the trust anchor. Two facts shape this design:
 *
 *  1. **OkHttp does NOT use `SSLContext.setDefault()`.** It builds its socket factory from the
 *     platform trust store unless handed an explicit `sslSocketFactory`. Installing a default
 *     context alone therefore leaves OkHttp (the extension HTTP chain) failing with
 *     `PKIX path building failed` while everything else works. [sslSocketFactory] is what fixes it.
 *  2. **Configuration arrives at runtime and ordering is not guaranteed.** Startup applies the proxy
 *     before `TrustManagerBridge` installs its own default. A snapshot manager would be clobbered
 *     or built too early. [ProxyAwareTrustManager] instead reads the current configuration on every
 *     check, so a single stable instance stays correct no matter when it is created or reconfigured.
 *
 * The CA is supplied as either an absolute `http(s)` URL (trawl serves it at
 * `http://<trawl-host>:8191/proxy-ca.crt`) or a PEM file path. A URL download uses a DIRECT
 * connection ([Proxy.NO_PROXY]) — the CA makes the proxy trusted, so fetching it THROUGH the proxy
 * would be circular.
 */
object ProxyTrustInstaller {
    private val logger = androidCompatLogger(ProxyTrustInstaller::class.java)
    private val lock = ReentrantLock()

    /** Extra accepted certificates (the proxy CA). Read live by the trust manager. */
    @Volatile
    private var extraAccepted: List<X509Certificate> = emptyList()

    /** Emergency override: accept any server certificate. Real security downgrade. */
    @Volatile
    private var ignoreAll: Boolean = false

    /**
     * The process-wide trust manager. Delegates to the platform (SKID-tolerant) manager and, in
     * addition, accepts chains rooted at the configured proxy CA. Stable identity so OkHttp can hold
     * it across reconfiguration.
     */
    private val proxyAwareTrustManager = ProxyAwareTrustManager()

    /** Trust manager to hand to OkHttp ([android okhttp3.OkHttpClient.Builder.sslSocketFactory]). */
    fun trustManager(): X509TrustManager = proxyAwareTrustManager

    /** Socket factory bound to [trustManager], for OkHttp. */
    fun sslSocketFactory(): SSLSocketFactory {
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf<TrustManager>(proxyAwareTrustManager), SecureRandom())
        return context.socketFactory
    }

    /**
     * Applies (or clears) the proxy CA configuration and installs it as the JVM default context.
     * Safe to call repeatedly; the live manager picks the change up immediately.
     */
    fun install(caCertPath: String, ignoreTlsErrors: Boolean) {
        lock.withLock {
            try {
                if (ignoreTlsErrors) {
                    logger.warn {
                        "ignoreTlsErrors is ENABLED: the JVM will accept invalid TLS certificates for " +
                            "ALL hosts, not only the proxy. Prefer installing the proxy CA."
                    }
                }

                ignoreAll = ignoreTlsErrors
                extraAccepted = if (ignoreTlsErrors || caCertPath.isBlank()) {
                    // ignoreAll covers the CA case; a blank path means "no CA".
                    emptyList()
                } else {
                    resolveCertificates(caCertPath).also { certs ->
                        if (certs.isEmpty()) {
                            logger.warn { "No certificates found in the proxy CA at '$caCertPath'." }
                        } else {
                            logger.info { "Trusting proxy CA from '$caCertPath' (${certs.size} certificate(s))." }
                        }
                    }
                }

                // Keep the JVM-wide default coherent for HttpsURLConnection and anything that relies
                // on it. OkHttp is covered separately via sslSocketFactory().
                val context = SSLContext.getInstance("TLS")
                context.init(null, arrayOf<TrustManager>(proxyAwareTrustManager), SecureRandom())
                SSLContext.setDefault(context)
                HttpsURLConnection.setDefaultSSLSocketFactory(context.socketFactory)
            } catch (t: Throwable) {
                // Never take the bridge down over CA installation.
                logger.warn(t) { "Failed to configure proxy CA trust." }
            }
        }
    }

    private fun resolveCertificates(source: String): List<X509Certificate> {
        val pemFile = if (isUrl(source)) downloadToCache(source) else File(source)

        if (!pemFile.exists()) {
            logger.warn { "Proxy CA file not found: ${pemFile.absolutePath}" }
            return emptyList()
        }

        return pemFile.inputStream().use { input ->
            CertificateFactory
                .getInstance("X.509")
                .generateCertificates(input)
                .filterIsInstance<X509Certificate>()
        }
    }

    private fun isUrl(source: String): Boolean =
        source.startsWith("http://", ignoreCase = true) || source.startsWith("https://", ignoreCase = true)

    private fun downloadToCache(url: String): File {
        val dir = File(System.getProperty("java.io.tmpdir"), "rensaio")
        dir.mkdirs()
        val target = File(dir, "trawl-ca.crt")

        // DIRECT connection: never route the CA download through the proxy it is meant to secure.
        val connection = URL(url).openConnection(Proxy.NO_PROXY) as HttpURLConnection
        connection.connectTimeout = 20_000
        connection.readTimeout = 20_000
        connection.requestMethod = "GET"

        if (connection.responseCode !in 200..299) {
            throw IllegalStateException("Proxy CA download from '$url' returned HTTP ${connection.responseCode}.")
        }

        connection.inputStream.use { input ->
            val tmp = File(dir, "trawl-ca.crt.tmp")
            tmp.outputStream().use { output -> input.copyTo(output) }
            if (!tmp.renameTo(target)) {
                tmp.copyTo(target, overwrite = true)
                tmp.delete()
            }
        }
        logger.info { "Downloaded proxy CA from '$url' to ${target.absolutePath}." }
        return target
    }

    /**
     * Live trust manager: consults [ignoreAll] / [extraAccepted] on every check, so late
     * configuration changes take effect without rebuilding OkHttp.
     */
    private class ProxyAwareTrustManager : X509TrustManager {
        override fun getAcceptedIssuers(): Array<X509Certificate> =
            (TrustManagerBridge.baseTrustManager()?.acceptedIssuers ?: emptyArray()) +
                extraAccepted.toTypedArray()

        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {
            val base = TrustManagerBridge.baseTrustManager()
                ?: throw CertificateException("No platform trust manager available.")
            base.checkClientTrusted(chain, authType)
        }

        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
            if (ignoreAll) {
                return
            }

            val base = TrustManagerBridge.baseTrustManager()
                ?: throw CertificateException("No platform trust manager available.")

            try {
                base.checkServerTrusted(chain, authType)
                return
            } catch (e: CertificateException) {
                // Fall through: the chain may terminate at the proxy CA instead of a public root.
                val anchor = chain.lastOrNull() ?: throw e
                if (extraAccepted.none { it == anchor }) {
                    throw e
                }
                // Trusted via the proxy CA; still verify the leaf is time-valid.
                (chain.firstOrNull() ?: throw e).checkValidity()
            }
        }
    }
}
