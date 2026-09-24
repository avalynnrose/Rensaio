using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mihon.ExtensionsBridge.Models.Abstractions;
using RensaioTray.Utils;

namespace RensaioTray
{
    public static class Extensions
    {
        /// <summary>
        /// Injects the Avalonia UI-thread CEF pump so the in-process extension runtime
        /// (IKVM/CEF) is driven from the tray's UI thread — the known-good desktop setup.
        /// </summary>
        public static IHostBuilder AddCefTimer(this IHostBuilder builder)
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartCefTimer, CefTimer>());
            return builder;
        }
    }
}
