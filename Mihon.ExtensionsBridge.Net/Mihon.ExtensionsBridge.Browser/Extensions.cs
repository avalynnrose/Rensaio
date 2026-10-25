using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// DI registration for the browser providers. The composition root (Rensaio.RPC)
    /// calls this so that <c>Mihon.ExtensionsBridge.Core</c> never takes an
    /// assembly reference on a browser engine.
    ///
    /// Every path is resolved from the DI <see cref="IWorkingFolderStructure"/> — the single source
    /// of truth — and is never passed in or configured.
    /// </summary>
    public static class Extensions
    {
        /// <summary>Registers both the Patchright (primary) and Playwright (fallback) providers.</summary>
        public static IServiceCollection AddBrowserProviders(this IServiceCollection services)
        {
            services.AddPatchrightProvider();
            services.AddPlaywrightProvider();
            return services;
        }

        /// <summary>Registers only the Patchright (primary) provider.</summary>
        public static IServiceCollection AddPatchrightProvider(this IServiceCollection services)
        {
            services.AddSingleton<PatchrightRuntimeInstaller>();
            services.AddSingleton<IBrowserRuntimeInstaller>(sp => sp.GetRequiredService<PatchrightRuntimeInstaller>());
            services.AddSingleton<IBrowserAutomationProvider, PatchrightBrowserProvider>();
            return services;
        }

        /// <summary>Registers only the Playwright (fallback) provider.</summary>
        public static IServiceCollection AddPlaywrightProvider(this IServiceCollection services)
        {
            services.AddSingleton<PlaywrightRuntimeInstaller>();
            services.AddSingleton<IBrowserRuntimeInstaller>(sp => sp.GetRequiredService<PlaywrightRuntimeInstaller>());
            services.AddSingleton<IBrowserAutomationProvider, PlaywrightBrowserProvider>();
            return services;
        }
    }

    /// <summary>
    /// Patchright runtime installer. Patchright is a drop-in <c>Microsoft.Playwright</c>
    /// replacement, so it shares the base implementation with a distinct engine identity.
    /// Install goes to <c>{AndroidFolder}/Patchright</c>, cache to <c>{TempFolder}/browser/Patchright</c>.
    /// </summary>
    public sealed class PatchrightRuntimeInstaller : PlaywrightBaseRuntimeInstaller
    {
        public PatchrightRuntimeInstaller(ILogger<PatchrightRuntimeInstaller> logger, IWorkingFolderStructure folders)
            : base(logger, folders, provider: "patchright", engine: "patchright")
        {
        }
    }

    /// <summary>Vanilla Playwright installer (fallback engine), same DI-supplied layout.</summary>
    public sealed class PlaywrightRuntimeInstaller : PlaywrightBaseRuntimeInstaller
    {
        public PlaywrightRuntimeInstaller(ILogger<PlaywrightRuntimeInstaller> logger, IWorkingFolderStructure folders)
            : base(logger, folders, provider: "playwright", engine: "playwright")
        {
        }
    }
}
