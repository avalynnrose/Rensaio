using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Helpers;
using Rensaio.RPC.Contracts;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// In-process implementation of <see cref="IExtensionRpcBridge"/> used on the desktop host
/// (where IKVM/CEF run inside the backend; see <c>AddExtensionsBridge</c>). Mirrors the
/// behavior the RPC host exposes over gRPC, but reads directly from the local runtime.
/// </summary>
public sealed class InProcessExtensionRpcBridge : IExtensionRpcBridge
{
    private readonly IBridgeManager _bridge;
    private readonly IWorkingFolderStructure _folders;

    public InProcessExtensionRpcBridge(IBridgeManager bridge, IWorkingFolderStructure folders)
    {
        _bridge = bridge;
        _folders = folders;
    }

    public async Task<List<RepositoryGroup>> ListExtensionsAsync(CancellationToken token = default)
    {
        await _bridge.InitializationCompleted.WaitAsync(token).ConfigureAwait(false);
        return _bridge.LocalExtensionManager.ListExtensions();
    }

    public async Task<List<SourceDescriptor>> ListSourceDescriptorsAsync(string package, CancellationToken token = default)
    {
        await _bridge.InitializationCompleted.WaitAsync(token).ConfigureAwait(false);

        RepositoryGroup? group = _bridge.LocalExtensionManager.ListExtensions()
            .FirstOrDefault(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, package, StringComparison.OrdinalIgnoreCase)));
        if (group == null)
            return [];

        IExtensionInterop interop = await _bridge.LocalExtensionManager.GetInteropAsync(group, token).ConfigureAwait(false);
        return interop.Sources.Select(s => new SourceDescriptor
        {
            PackageId = package,
            SourceId = s.Id,
            Name = s.Name,
            Language = s.Language,
            SupportsLatest = s.SupportsLatest,
            IsConfigurable = s.IsConfigurableSource,
            IsHttpSource = s.IsHttpSource,
            IsParsedHttpSource = s.IsParsedHttpSource,
            IsCatalogueSource = s.IsCatalogueSource,
        }).ToList();
    }

    public Task<Stream?> OpenExtensionIconAsync(string packageId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return Task.FromResult<Stream?>(null);

        RepositoryEntry? entry = _bridge.LocalExtensionManager.ListExtensions()
            .SelectMany(g => g.Entries)
            .FirstOrDefault(e => string.Equals(e.Extension?.Package, packageId, StringComparison.OrdinalIgnoreCase));

        if (entry?.Icon?.FileName == null)
            return Task.FromResult<Stream?>(null);

        // Resolve the icon relative to the extension's versioned folder (must be the
        // extension version, or the file will not exist). Models-only helper (IKVM-free).
        string folder = _folders.GetExtensionVersionFolder(entry);
        string path = Path.Combine(folder, entry.Icon.FileName);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(File.OpenRead(path));
    }

    public async Task<ISourceInterop> GetSourceInteropAsync(string package, long sourceId, CancellationToken token = default)
    {
        await _bridge.InitializationCompleted.WaitAsync(token).ConfigureAwait(false);

        RepositoryGroup? group = _bridge.LocalExtensionManager.ListExtensions()
            .FirstOrDefault(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, package, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"Extension '{package}' not found.");

        IExtensionInterop interop = await _bridge.LocalExtensionManager.GetInteropAsync(group, token).ConfigureAwait(false);
        return interop.Sources.FirstOrDefault(s => s.Id == sourceId)
            ?? throw new InvalidOperationException($"Source {sourceId} not found in {package}.");
    }
}
