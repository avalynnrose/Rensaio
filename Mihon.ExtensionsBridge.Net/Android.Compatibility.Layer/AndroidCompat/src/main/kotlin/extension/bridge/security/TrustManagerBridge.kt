package extension.bridge.security

import extension.bridge.logging.androidCompatLogger
import java.security.KeyStore
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.CertificateParsingException
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

object TrustManagerBridge {
    private val logger = androidCompatLogger(TrustManagerBridge::class.java)
    private val installed = AtomicBoolean(false)
    private val lock = Any()

    /**
     * Stable platform-backed manager. Exposed so other trust compositions (notably
     * [ProxyTrustInstaller], which OkHttp consumes) can DELEGATE to the real platform store instead
     * of capturing a snapshot that a later `SSLContext.setDefault` would invalidate.
     */
    @Volatile
    private var base: X509TrustManager? = null

    /** The underlying platform trust manager (SKID tolerance is a separate, composing concern). */
    fun baseTrustManager(): X509TrustManager? {
        base?.let { return it }
        synchronized(lock) {
            base?.let { return it }
            val created = loadPlatformTrustManager()
            base = created
            return created
        }
    }

    fun ensureSubjectKeyIdentifierTolerance() {
        if (installed.get()) {
            return
        }
        synchronized(lock) {
            if (installed.get()) {
                return
            }
            runCatching {
                installSubjectKeyIdentifierTolerantTrustManager()
            }.onSuccess {
                installed.set(true)
                logger.info { "Installed SubjectKeyIdentifier tolerant trust manager" }
            }.onFailure { throwable ->
                logger.warn(throwable) { "Failed to install SubjectKeyIdentifier tolerant trust manager" }
            }
        }
    }

    private fun loadPlatformTrustManager(): X509TrustManager? {
        val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        factory.init(null as KeyStore?)
        return factory.trustManagers.filterIsInstance<X509TrustManager>().firstOrNull()
    }

    private fun installSubjectKeyIdentifierTolerantTrustManager() {
        val defaultTm = baseTrustManager() ?: return
        val tolerantTm =
            object : X509TrustManager {
                override fun getAcceptedIssuers(): Array<X509Certificate> = defaultTm.acceptedIssuers

                override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {
                    defaultTm.checkClientTrusted(chain, authType)
                }

                override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                    try {
                        defaultTm.checkServerTrusted(chain, authType)
                    } catch (e: CertificateException) {
                        val skidCause =
                            generateSequence<Throwable>(e) { it.cause }
                                .firstOrNull { it is CertificateParsingException && it.message?.contains("SubjectKeyIdentifier") == true }
                                ?: throw e
                        val leaf = chain.firstOrNull() ?: throw skidCause
                        leaf.checkValidity()
                    }
                }
            }
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf<TrustManager>(tolerantTm), SecureRandom())
        SSLContext.setDefault(context)
        HttpsURLConnection.setDefaultSSLSocketFactory(context.socketFactory)
    }
}
