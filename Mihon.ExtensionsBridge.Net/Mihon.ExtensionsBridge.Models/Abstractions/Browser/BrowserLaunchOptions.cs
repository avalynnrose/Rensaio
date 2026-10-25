namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Options used to launch a browser session. One session == one isolated browser context.
    /// </summary>
    public sealed class BrowserLaunchOptions
    {
        public bool Headless { get; init; } = true;

        public bool Persistent { get; init; }

        /// <summary>Browser channel to use (e.g. <c>chrome</c> to reuse the system Chrome).</summary>
        public string? Channel { get; init; }

        /// <summary>
        /// Proxy server URL applied to Chromium (e.g. <c>http://host:8192</c> for trawl, or
        /// <c>socks5://host:port</c>). Chromium issues <c>CONNECT host:443</c> over plain HTTP to
        /// an HTTP proxy for HTTPS URLs, which is exactly the trawl transparent-proxy model.
        /// </summary>
        public string? ProxyUrl { get; init; }

        public string? ProxyUsername { get; init; }

        public string? ProxyPassword { get; init; }

        /// <summary>
        /// The proxy's root CA (URL or PEM path) that must be trusted by the browser. A
        /// TLS-terminating proxy such as trawl re-signs every connection with a per-host leaf, so
        /// its CA has to be installed into the browser trust store (there is no stable leaf to
        /// pin). See the trawl proxy plan for the OS-store / NSS-import strategy.
        /// </summary>
        public string? CaCertSource { get; init; }

        /// <summary>
        /// Emergency override: trust the proxy's interception without installing its CA. Real
        /// security downgrade; off by default.
        /// </summary>
        public bool IgnoreTlsErrors { get; init; }

        public string? UserAgent { get; init; }

        public int MaxConcurrency { get; init; } = 4;
    }
}
