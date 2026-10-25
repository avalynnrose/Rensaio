using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// Fallback provider. It is selected via <c>Browser.FallbackProviders</c> or by setting
    /// <c>Browser.Provider = "playwright"</c>. Unlike the Patchright provider it does not advertise
    /// <see cref="BrowserProviderCapabilities.Stealth"/> and it honours a caller-supplied User-Agent,
    /// so it behaves as plain Chromium automation.
    ///
    /// Note on packaging: Patchright and Microsoft.Playwright both ship an assembly named
    /// <c>Microsoft.Playwright</c>, so only one engine can be loaded per process. To make this
    /// provider use the vanilla engine, change the <c>PackageReference</c> in
    /// <c>Mihon.ExtensionsBridge.Browser.csproj</c> from <c>Patchright</c> to
    /// <c>Microsoft.Playwright</c>; no code change is required.
    /// </summary>
    public sealed class PlaywrightBrowserProvider : PlaywrightProviderBase
    {
        public PlaywrightBrowserProvider(
            ILogger<PlaywrightBrowserProvider> logger,
            PlaywrightRuntimeInstaller installer,
            IWorkingFolderStructure folders)
            : base(logger, stealth: false, installer, folders)
        {
        }

        /// <inheritdoc />
        public override string Name => "playwright";

        /// <inheritdoc />
        public override BrowserProviderCapabilities Capabilities =>
            BrowserProviderCapabilities.Fetch |
            BrowserProviderCapabilities.LiveDom |
            BrowserProviderCapabilities.RequestIntercept |
            BrowserProviderCapabilities.Cookies |
            BrowserProviderCapabilities.Screenshot |
            BrowserProviderCapabilities.PersistentProfile |
            BrowserProviderCapabilities.Headful;
    }
}
