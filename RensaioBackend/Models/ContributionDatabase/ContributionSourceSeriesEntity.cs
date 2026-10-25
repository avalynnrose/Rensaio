using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Many-to-many association between a series entry (<see cref="ContributionSeriesEntity"/>) and
    /// the sources (<see cref="ContributionSourceEntity"/>) that contribute it.
    ///
    /// A series entry is identified by <c>package:url</c> (see
    /// <see cref="ContributionSeriesEntity.DeriveId(string, string)"/>), so several sources of the
    /// SAME package that expose the same url share ONE series entry and are represented by one row
    /// here per <c>(source, series entry)</c> pair. The composite key is
    /// <c>(SourceId, SeriesEntityId)</c>.
    ///
    /// Carries its own <see cref="Version"/> replication counter so an association can be added or
    /// tombstoned independently of the series entry it links (mirrors every other contribution row).
    /// </summary>
    public class ContributionSourceSeriesEntity : IVersionedRow
    {
        /// <summary>BLOB(16) FK → ContributionSourceEntity.Id.</summary>
        [JsonPropertyName("s")]
        public Guid SourceId { get; set; }

        /// <summary>BLOB(16) FK → ContributionSeriesEntity.Id.</summary>
        [JsonPropertyName("e")]
        public Guid SeriesEntityId { get; set; }

        /// <summary>Replication version counter (0 add/update, -1 delete, -2/-3 uploaded).</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual ContributionSourceEntity? Source { get; set; }

        public virtual ContributionSeriesEntity? SeriesEntity { get; set; }
    }
}
