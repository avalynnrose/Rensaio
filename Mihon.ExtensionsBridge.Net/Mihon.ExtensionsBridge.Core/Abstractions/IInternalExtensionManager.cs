using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Abstractions
{
    public interface IInternalExtensionManager : IExtensionManager
    {
        Task<int> ValidateAndRecompileAsync(IEnumerable<RepositoryEntry> entries, CancellationToken token = default);
        Task InitializeAsync(CancellationToken token = default);
        RepositoryGroup? FindExtension(RepositoryGroup grp);
        Task CompareOnlineWithLocalAndAutoUpdateAsync(IEnumerable<TachiyomiRepository> onlineRepos, CancellationToken token = default);
        Task ShutdownAsync(CancellationToken token = default);

        /// <summary>
        /// Prepares the given package for use, whether or not it is installed. Installed (or
        /// already-prepared) packages are returned as-is; an online-only package is downloaded,
        /// compiled and registered as a <see cref="RepositoryGroup.Temporary"/> group.
        /// </summary>
        Task<RepositoryGroup?> PrepareExtensionAsync(string package, bool force = false, CancellationToken token = default);

        /// <summary>
        /// Promotes a temporary group to installed (<see cref="RepositoryGroup.Temporary"/> set to
        /// false) without re-downloading, since the artifacts already exist. Returns the group, or
        /// null when the package is not present.
        /// </summary>
        Task<RepositoryGroup?> PromoteExtensionAsync(string package, CancellationToken token = default);

        /// <summary>
        /// Releases a temporary group prepared for a sweep: disposes its cached interop and
        /// removes it. Installed (non-temporary) groups are never affected.
        /// </summary>
        Task<bool> ReleaseExtensionAsync(string package, CancellationToken token = default);

        /// <summary>Releases every temporary group (sweep teardown / startup reclamation).</summary>
        Task<int> ReleaseAllTemporaryExtensionsAsync(CancellationToken token = default);
        /// <summary>Evicts idle/over-budget extension interops so their native classloaders and JVM heaps can be released.</summary>
        Task SweepIdleInteropsAsync();
        /// <summary>
        /// Evicts idle/over-budget extension interops using explicit thresholds. Rensaio.RPC
        /// drives its own configurable LRU policy through this overload; the parameterless
        /// version keeps the historical hardcoded defaults.
        /// </summary>
        Task SweepIdleInteropsAsync(TimeSpan idleTimeout, int maxCount);
        /// <summary>Snapshot of the interop cache for LRU telemetry (loaded count and eviction counters).</summary>
        InteropCacheStats GetInteropCacheStats();
    }
}