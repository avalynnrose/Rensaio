using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mihon.ExtensionsBridge.Core.Browser;
using Mihon.ExtensionsBridge.Core.Runtime;
using Mihon.ExtensionsBridge.Core.Services;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Mihon.ExtensionsBridge.Core.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Extensions
{
    public static class Extensions
    {
        public static IServiceCollection AddExtensionsBridge(this IServiceCollection services)
        {
            services.AddSingleton<IWorkingFolderStructure, WorkingFolderStructure>();
            services.AddSingleton<IDex2JarConverter, Dex2JarConverter>();
            services.AddScoped<IRepositoryDownloader, RepositoryDownloader>();
            services.AddHttpClient(nameof(RepositoryDownloader));
            services.AddSingleton<IInternalRepositoryManager, RepositoryManager>();
            services.AddSingleton<IInternalExtensionManager, ExtensionManager>();
            services.AddSingleton<IRepositoryManager, PublicProxyRepositoryManager>();
            services.AddSingleton<IExtensionManager, PublicProxyExtensionManager>();
            services.AddSingleton<IBridgeManager, BridgeManager>();
            services.AddHostedService<BridgeHost>();

            // Browser automation orchestration lives in Core; the engine itself is supplied by a
            // provider project (see Mihon.ExtensionsBridge.Browser) so Core never references a
            // browser stack. Providers registered in DI are collected into the registry.
            services.AddSingleton<IBrowserProviderRegistry>(sp =>
            {
                var registry = new BrowserProviderRegistry();
                foreach (var provider in sp.GetServices<IBrowserAutomationProvider>())
                {
                    registry.Register(provider);
                }
                return registry;
            });
            // Registered with an explicit factory: the browser roots (install = AndroidFolder,
            // cache = TempFolder) come from the host-supplied IWorkingFolderStructure, and the
            // runtime installers are collected from DI. Resolving these lazily here avoids relying
            // on convention-based constructor selection for the optional parameters.
            services.AddSingleton<IBrowserAutomationService>(sp => new BrowserAutomationService(
                sp.GetRequiredService<IBrowserProviderRegistry>(),
                sp.GetRequiredService<ILogger<BrowserAutomationService>>(),
                sp.GetRequiredService<IWorkingFolderStructure>(),
                preferencesAccessor: null,
                installers: sp.GetServices<IBrowserRuntimeInstaller>()));
            return services;
        }
    }
}
