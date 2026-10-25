using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// First provider: Patchright (anti-detect Chromium). Patchright ships a drop-in
    /// <c>Microsoft.Playwright.dll</c>, so this class only declares the engine identity; all
    /// behaviour lives in <see cref="PlaywrightProviderBase"/>.
    /// </summary>
    public sealed class PatchrightBrowserProvider : PlaywrightProviderBase
    {
        public PatchrightBrowserProvider(
            ILogger<PatchrightBrowserProvider> logger,
            PatchrightRuntimeInstaller installer,
            IWorkingFolderStructure folders)
            : base(logger, stealth: true, installer, folders)
        {
        }

        /// <inheritdoc />
        public override string Name => "patchright";

        /// <inheritdoc />
        public override BrowserProviderCapabilities Capabilities =>
            BrowserProviderCapabilities.Fetch |
            BrowserProviderCapabilities.LiveDom |
            BrowserProviderCapabilities.RequestIntercept |
            BrowserProviderCapabilities.Cookies |
            BrowserProviderCapabilities.Screenshot |
            BrowserProviderCapabilities.PersistentProfile |
            BrowserProviderCapabilities.Stealth  |
            BrowserProviderCapabilities.Headful;
    }
}
