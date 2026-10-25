using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Schema v2 record serialized as the <c>Data</c> column of a contribution series entry.
    /// This is the CURRENT record format managed by the contributor database going forward.
    ///
    /// Differences from <see cref="ContributionRecordV1"/>:
    ///   * <c>r</c> (<see cref="Url"/>) — the canonical source url the entry was derived from.
    ///     It is now part of the record payload rather than being implied by the row identity,
    ///     so a decoded record is self-describing.
    ///
    /// Source identity/display metadata (Package, SourceId, SourceName, SourceLanguage,
    /// LastBatchExecutionUTC) still live on <see cref="ContributionSourceEntity"/> and are not
    /// part of this per-series payload.
    /// </summary>
    public class ContributionRecordV2
    {
        /// <summary>The schema version managed from now on.</summary>
        public const int CurrentSchemaVersion = 2;

        [JsonPropertyName("v")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>The source url this entry was derived from.</summary>
        [JsonPropertyName("r")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("i")]
        public Guid TitleId { get; set; }
        [JsonPropertyName("s")]
        public int Status { get; set; }
        [JsonPropertyName("t")]
        public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("m")]
        public string? Memo { get; set; }
        [JsonPropertyName("p")]
        public bool SeenInPopular { get; set; }
        [JsonPropertyName("e")]
        public bool SeenInLatest { get; set; }
        [JsonPropertyName("l")]
        public decimal? LastChapter { get; set; }
        [JsonPropertyName("u")]
        public DateTime? LastUpdateUTC { get; set; }

        /// <summary>
        /// True when the payload carries a legacy (<see cref="ContributionRecordV1"/>) schema
        /// version. Such a record is no longer managed: any <see cref="ContributionSeriesEntity"/>
        /// holding it is automatically tombstoned (see <c>ContributionDbContext</c>) and later
        /// regenerated as a v2 record by the normal sweep.
        /// </summary>
        [JsonIgnore]
        public bool IsLegacy => SchemaVersion < CurrentSchemaVersion;

        /// <summary>
        /// True when the two payloads differ on any NON-EXEMPT (material) field. The exempt fields
        /// (<see cref="Status"/>, <see cref="SeenInPopular"/>, <see cref="SeenInLatest"/>,
        /// <see cref="LastChapter"/>, <see cref="LastUpdateUTC"/>) are routine scan noise and never
        /// count as a material change. Used to decide whether a graph change invalidates a series
        /// verification (see <c>ContributionDbContext.InvalidateVerificationOnGraphChanges</c>).
        /// </summary>
        public static bool HasMaterialChange(ContributionRecordV2? current, ContributionRecordV2? original)
        {
            if (ReferenceEquals(current, original)) return false;
            if (current is null || original is null) return true;
            return current.SchemaVersion != original.SchemaVersion
                || !string.Equals(current.Url, original.Url, StringComparison.Ordinal)
                || current.TitleId != original.TitleId
                || !string.Equals(current.ThumbnailUrl, original.ThumbnailUrl, StringComparison.Ordinal)
                || !string.Equals(current.Memo, original.Memo, StringComparison.Ordinal);
        }
    }
}
