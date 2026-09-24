namespace Mihon.ExtensionsBridge.Core.Abstractions
{
    /// <summary>
    /// Point-in-time snapshot of the extension interop cache, exposed for the RPC
    /// LRU telemetry surface. Read-only; does not touch any locking.
    /// </summary>
    public readonly struct InteropCacheStats
    {
        public InteropCacheStats(int loadedCount, long idleEvictions, long overCapacityEvictions)
        {
            LoadedCount = loadedCount;
            IdleEvictions = idleEvictions;
            OverCapacityEvictions = overCapacityEvictions;
        }

        /// <summary>Number of interops currently cached (i.e. loaded).</summary>
        public int LoadedCount { get; }

        /// <summary>Total interops evicted because they were idle beyond the timeout.</summary>
        public long IdleEvictions { get; }

        /// <summary>Total interops evicted because the cache exceeded the configured cap.</summary>
        public long OverCapacityEvictions { get; }
    }
}
