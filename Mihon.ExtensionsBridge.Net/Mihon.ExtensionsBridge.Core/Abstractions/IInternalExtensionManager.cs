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