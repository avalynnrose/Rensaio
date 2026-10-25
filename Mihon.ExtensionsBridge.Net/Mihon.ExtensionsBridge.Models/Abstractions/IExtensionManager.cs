using Mihon.ExtensionsBridge.Models;

namespace Mihon.ExtensionsBridge.Models.Abstractions
{
    public interface IExtensionManager
    {
        Task<RepositoryGroup?> AddExtensionAsync(TachiyomiExtension extension, bool force = false, CancellationToken token = default);
        Task<RepositoryGroup?> AddExtensionAsync(TachiyomiRepository repository, TachiyomiExtension extension, bool force = false, CancellationToken token = default);
        Task<RepositoryGroup?> AddExtensionAsync(byte[] apk, bool force = false, CancellationToken token = default);
        Task<IExtensionInterop> GetInteropAsync(RepositoryGroup entry, CancellationToken token = default);
        /// <summary>
        /// Returns the local repository groups. Temporary groups (online extensions prepared
        /// for a sweep) are excluded unless <paramref name="includeTemporary"/> is true.
        /// </summary>
        List<RepositoryGroup> ListExtensions(bool includeTemporary = false);
        /// <summary>
        /// Finds a group by name. Temporary groups are excluded unless
        /// <paramref name="includeTemporary"/> is true.
        /// </summary>
        RepositoryGroup? FindExtension(string name, bool includeTemporary = false);
        Task<bool> RemoveExtensionAsync(RepositoryGroup group, CancellationToken token = default);
        Task<RepositoryGroup?> RemoveExtensionVersionAsync(RepositoryEntry entry, CancellationToken token = default);
        Task<RepositoryGroup> SetActiveExtensionVersionAsync(RepositoryGroup group, CancellationToken token = default);

    }
}