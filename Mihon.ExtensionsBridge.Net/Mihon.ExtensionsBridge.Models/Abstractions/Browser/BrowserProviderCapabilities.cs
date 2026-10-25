using System;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// What a <see cref="IBrowserAutomationProvider"/> can actually do. Callers must degrade
    /// gracefully when a requested capability is not advertised by the resolved provider.
    /// </summary>
    [Flags]
    public enum BrowserProviderCapabilities
    {
        None = 0,

        /// <summary>Solver fast path (Tier 1): execute a fetch inside a browser context.</summary>
        Fetch = 1 << 0,

        /// <summary>Live-DOM tier (Tier 2): navigate, evaluate scripts and expose bindings.</summary>
        LiveDom = 1 << 1,

        /// <summary>Route/block/rewrite outbound requests.</summary>
        RequestIntercept = 1 << 2,

        /// <summary>Read and write browser cookies.</summary>
        Cookies = 1 << 3,

        /// <summary>Capture page screenshots.</summary>
        Screenshot = 1 << 4,

        /// <summary>Reuse a persistent on-disk profile between launches.</summary>
        PersistentProfile = 1 << 5,

        /// <summary>Anti-detect / stealth patches are present in the underlying engine.</summary>
        Stealth = 1 << 6,

        /// <summary>Can run with a visible window (headful).</summary>
        Headful = 1 << 7,
    }
}
