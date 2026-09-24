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
    /// <summary>Installed extensions (groups + entries), as the host sees them.</summary>
    Task<List<RepositoryGroup>> ListExtensionsAsync(CancellationToken token = default);

    /// <summary>Source descriptors for one extension package.</summary>
    Task<List<SourceDescriptor>> ListSourceDescriptorsAsync(string package, CancellationToken token = default);

    /// <summary>Resolves one source in a package to a callable interop.</summary>
    Task<ISourceInterop> GetSourceInteropAsync(string package, long sourceId, CancellationToken token = default);

    /// <summary>Streams the extension's icon PNG, or null when unavailable.</summary>
    Task<Stream?> OpenExtensionIconAsync(string packageId, CancellationToken token = default);
}
