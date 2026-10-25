package extension.bridge

/**
 * Holds proxy configuration mirrored from [SettingsConfig.Settings].
 *
 * Originally SOCKS-only. It now also carries a [proxyScheme] ("socks5" or "http") plus the
 * proxy CA details, because a transparent proxy such as trawl is HTTP-only AND
 * TLS-terminating:
 *  - "http" must be selected for trawl; it must never be used as SOCKS,
 *  - [caCertPath] (URL or PEM path) is the trust anchor for its per-host leaf certificates.
 */
data class ProxySettings(
    val proxyEnabled: Boolean,
    val socksProxyVersion: Int,
    val proxyHost: String,
    val proxyPort: String,
    val proxyUsername: String,
    val proxyPassword: String,
    val proxyScheme: String = "socks5",
    val caCertPath: String = "",
    val ignoreTlsErrors: Boolean = false,
)

fun SettingsConfig.Settings.toProxySettings(): ProxySettings =
    ProxySettings(
        proxyEnabled = socksProxyEnabled,
        socksProxyVersion = socksProxyVersion,
        proxyHost = socksProxyHost,
        proxyPort = socksProxyPort,
        proxyUsername = socksProxyUsername,
        proxyPassword = socksProxyPassword,
        proxyScheme = proxyScheme,
        caCertPath = caCertPath,
        ignoreTlsErrors = ignoreTlsErrors,
    )
