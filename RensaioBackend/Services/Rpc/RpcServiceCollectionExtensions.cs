using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mihon.ExtensionsBridge.Models.Abstractions;
using RensaioBackend.Utils;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Registers the RPC-backed extension stack.
///
/// This replaces the old in-process <c>AddExtensionsBridge()</c>: instead of
/// hosting IKVM/CEF/Mihon inside the backend, the backend now supervises the
/// isolated Rensaio.RPC process and talks to it over gRPC. Works identically
/// for the tray and Docker deployments because the supervisor lives in the
/// backend, not the tray.
/// </summary>
public static class RpcServiceCollectionExtensions
{
    public static IServiceCollection AddRpcBridge(this IServiceCollection services)
    {
        // Folder layout: the backend only needs paths for display (icon paths);
        // RPC owns the real files.
        services.TryAddSingleton<IWorkingFolderStructure, LocalWorkingFolderStructure>();

        // Process lifecycle + readiness gate (single instance shared by all callers).
        services.TryAddSingleton<RpcProcessSupervisor>();
        services.TryAddSingleton<IRpcReadinessGate>(sp => sp.GetRequiredService<RpcProcessSupervisor>());
        // Lifecycle notifications so the client can gate on BRIDGE readiness (not just
        // the listening handshake) and reset that gate when the process restarts.
        services.TryAddSingleton<IRpcProcessSupervisor>(sp => sp.GetRequiredService<RpcProcessSupervisor>());

        // The gRPC-backed bridge replacing the in-process implementation.
        services.TryAddSingleton<RpcBridgeClient>();
        services.TryAddSingleton<IBridgeManager>(sp => sp.GetRequiredService<RpcBridgeClient>());
        services.TryAddSingleton<Mihon.ExtensionsBridge.Models.Abstractions.IExtensionManager>(
            sp => sp.GetRequiredService<RpcBridgeClient>());
        services.TryAddSingleton<Mihon.ExtensionsBridge.Models.Abstractions.IRepositoryManager>(
            sp => sp.GetRequiredService<RpcBridgeClient>());

        // Supervisor runs as a hosted service so it starts with the app and stops
        // (gracefully) with it. It is NOT dependent on anything the tray injects.
        services.AddHostedService(sp =>
        {
            var supervisor = sp.GetRequiredService<RpcProcessSupervisor>();
            // Pass through the working folders so the child process lands on the
            // same extension/temp layout the backend expects.
            supervisor.BridgeFolder = EnvironmentSetup.Configuration?["BridgeFolder"] ?? "extensions";
            supervisor.TempFolder = EnvironmentSetup.Configuration?["TempFolder"];
            return supervisor;
        });

        return services;
    }
}
