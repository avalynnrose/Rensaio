using System;
using System.Collections.Generic;
using System.Text;

namespace Mihon.ExtensionsBridge.Models
{
    public class FlareSolverrPreferences
    {
        public bool Enabled { get; set; } = false;
        public string Url { get; set; } = "http://localhost:8191";
        public int Timeout { get; set; } = 60;
        public string SessionName { get; set; } = "extension.bridge";
        public int SessionTtl { get; set; } = 15;
        public bool AsResponseFallback { get; set; } = false;
    }
    public class SocksProxyPreferences
    {
        public bool Enabled { get; set; } = false;
        public int Version { get; set; } = 5;
        public string Host { get; set; } = "";
        public int Port { get; set; } = 0;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>
        /// Transport scheme for the proxy: <c>"socks5"</c> (default, preserves the historical
        /// behaviour) or <c>"http"</c>.
        ///
        /// trawl's transparent proxy is <b>HTTP-only</b> (both HTTP and HTTPS are
        /// <c>http://host:8192</c>, plain HTTP even for HTTPS CONNECT) and explicitly must NOT be
        /// configured as SOCKS, so selecting trawl requires this to be <c>"http"</c>.
        ///
        /// The same host/port cannot serve the JVM SOCKS path and the browser HTTP path at once:
        /// switching scheme MOVES the JVM chain from SOCKS to HTTP rather than adding to it, and
        /// takes effect on the next RPC-host restart.
        /// </summary>
        public string Scheme { get; set; } = "socks5";

        /// <summary>
        /// The proxy's root CA, used because a transparent proxy such as trawl is
        /// <b>TLS-terminating</b> and re-signs every connection with a per-host leaf.
        ///
        /// Accepts EITHER:
        /// <list type="bullet">
        /// <item>an absolute <c>http</c>/<c>https</c> URL — trawl serves its CA at
        /// <c>http://<trawl-host>:8191/proxy-ca.crt</c>; it is downloaded at runtime and
        /// cached under the host temp folder, or</item>
        /// <item>a filesystem path to a PEM file.</item>
        /// </list>
        ///
        /// Detection is by absolute-URI parse, never by file extension.
        /// </summary>
        public string CaCertPath { get; set; } = "";

        /// <summary>
        /// Emergency override: trust the proxy's TLS interception without installing its CA.
        /// This disables certificate verification and is a real security downgrade, so it is off
        /// by default and logged loudly when enabled. Prefer <see cref="CaCertPath"/>.
        /// </summary>
        public bool IgnoreTlsErrors { get; set; } = false;
    }
    /// <summary>
    /// Preferences for the pluggable .NET-hosted browser automation subsystem (plan:
    /// browser-automation-core-plan.md). Mirrors the FlareSolverr/Cef shape so it can be pushed
    /// into the AndroidCompat settings the same way.
    /// </summary>
    public class BrowserAutomationPreferences
    {
        /// <summary>
        /// Master switch. When false, the whole subsystem is never initialised and WebView-backed
        /// sources fall back to the direct network chain / FlareSolverr. Requires restart.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>"patchright" | "playwright" | "camoufox" | "cdp" | "auto".</summary>
        public string Provider { get; set; } = "patchright";

        /// <summary>Ordered fallbacks if the primary provider is unavailable or fails.</summary>
        public List<string> FallbackProviders { get; set; } = ["playwright"];

        /// <summary>
        /// "auto" = pick a runner per request (solver first, live DOM only when needed).
        /// Otherwise "solver" | "webview".
        /// </summary>
        public string Backend { get; set; } = "auto";

        public bool Headless { get; set; } = true;

        public bool Stealth { get; set; } = true;

        /// <summary>Browser channel to use, e.g. "chrome" to reuse the system Chrome.</summary>
        public string? Channel { get; set; }

        /// <summary>Maximum number of concurrent pages/sessions (replaces Cef.MaxRenderers).</summary>
        public int MaxConcurrency { get; set; } = 4;

        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>Idle timeout (ms) before an unused pooled session is destroyed.</summary>
        public long IdleTimeoutMs { get; set; } = 300_000;

        /// <summary>Whether session pooling/reuse across requests is enabled.</summary>
        public bool PoolEnabled { get; set; } = true;

        /// <summary>
        /// When true, the runtime (browser build) is installed at warm-up if missing. When false,
        /// the engine installs lazily on first launch, or fails and degrades to the direct chain.
        /// </summary>
        /// <remarks>
        /// All directories (install under <c>AndroidFolder/{Provider}</c>, profile/cache under
        /// <c>TempFolder</c>) come from the DI <c>IWorkingFolderStructure</c>; they are deliberately
        /// NOT configurable here, so there is a single source of truth for paths.
        /// </remarks>
        public bool AutoInstall { get; set; } = true;

        /// <summary>
        /// Per-provider opaque settings bag (so new providers need no schema change).
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> ProviderOptions { get; set; } = [];
    }

    // NOTE: CefPreferences was removed with JCEF. Its former knobs map onto
    // BrowserAutomationPreferences: MaxRenderers -> MaxConcurrency, IdleTimeoutMs -> IdleTimeoutMs,
    // WebViewPoolEnabled -> PoolEnabled, Enabled -> Enabled. The CEF message-pump intervals are
    // deleted outright (the .NET-hosted browser drives itself; there is no pump to tune).

    public class Preferences
    {
        public FlareSolverrPreferences FlareSolverr { get; set; } = new FlareSolverrPreferences();

        public SocksProxyPreferences SocksProxy { get; set; } = new SocksProxyPreferences();

        /// <summary>
        /// Pluggable .NET-hosted browser automation. See <see cref="BrowserAutomationPreferences"/>.
        /// </summary>
        public BrowserAutomationPreferences Browser { get; set; } = new BrowserAutomationPreferences();

        public Dictionary<string, Dictionary<string, string>> Interceptors = [];
    }
}
