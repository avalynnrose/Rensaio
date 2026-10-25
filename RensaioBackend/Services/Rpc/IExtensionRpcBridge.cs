using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Rensaio.RPC.Contracts;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Surface several backend services need that is provided differently depending on how the
/// extension runtime is hosted:
///  - Docker/headless: by the isolated <c>Rensaio.RPC</c> child (gRPC) — implemented by
///    <see cref="RpcBridgeClient"/>.
///  - Desktop: by the in-process runtime (<c>AddExtensionsBridge</c>) — implemented by
///    <see cref="InProcessExtensionRpcBridge"/>.
///
/// These members are intentionally separate from <c>IBridgeManager</c>/<c>IExtensionManager</c>
/// because they are transport concerns (icon streaming, source descriptors) that each host
/// fulfills its own way.
/// </summary>
public interface IExtensionRpcBridge
{
    /// <summary>
    /// Installed extensions (groups + entries), as the host sees them. Temporary (online-prepared)
    /// groups are excluded unless <paramref name="includeTemporary"/> is true.
    /// </summary>
    Task<List<RepositoryGroup>> ListExtensionsAsync(bool includeTemporary = false, CancellationToken token = default);

    /// <summary>Repositories currently known online, each with the extensions they expose.</summary>
    Task<List<TachiyomiRepository>> ListOnlineRepositoriesAsync(CancellationToken token = default);

    /// <summary>Source descriptors for one extension package.</summary>
    Task<List<SourceDescriptor>> ListSourceDescriptorsAsync(string package, CancellationToken token = default);

    /// <summary>Resolves one source in a package to a callable interop.</summary>
    Task<ISourceInterop> GetSourceInteropAsync(string package, long sourceId, CancellationToken token = default);

    /// <summary>Streams the extension's icon PNG, or null when unavailable.</summary>
    Task<Stream?> OpenExtensionIconAsync(string packageId, CancellationToken token = default);

    /// <summary>
    /// Prepares an online extension for a sweep (download/compile + register as temporary),
    /// whether or not it is installed. Returns the descriptor, or null when the package is not
    /// available online.
    /// </summary>
    Task<OnlineExtensionDescriptor?> PrepareOnlineExtensionAsync(string package, CancellationToken token = default);

    /// <summary>Releases a temporary extension previously prepared for a sweep.</summary>
    Task<bool> ReleaseOnlineExtensionAsync(string package, CancellationToken token = default);

    /// <summary>Releases every temporary extension prepared for a sweep.</summary>
    Task<int> ReleaseAllTemporaryExtensionsAsync(CancellationToken token = default);
}
