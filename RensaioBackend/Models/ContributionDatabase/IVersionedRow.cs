namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Implemented by every contribution-database row that carries the replication
    /// <c>Version</c> cursor (titles, series-titles, series entries, sources, metadata).
    /// Lets the shared retirement helper in
    /// <see cref="RensaioBackend.Services.Contributions.ContributionRowLifecycle"/> decide,
    /// from the current version alone, whether a no-longer-wanted row must be tombstoned or
    /// can simply be deleted.
    ///
    /// EF Core maps only the concrete <c>Version</c> property on each entity; this interface
    /// adds no column and no mapping.
    /// </summary>
    public interface IVersionedRow
    {
        /// <summary>Replication version cursor (see <see cref="RensaioBackend.Services.Contributions.ContributionVersions"/>).</summary>
        int Version { get; set; }
    }
}
