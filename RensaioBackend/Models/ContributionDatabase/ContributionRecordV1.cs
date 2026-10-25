using RensaioBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// LEGACY schema v1 record. Superseded by <see cref="ContributionRecordV2"/>.
    ///
    /// This type is kept only so legacy (v1) payloads can still be deserialized. From now on
    /// the contributor database manages <see cref="ContributionRecordV2"/> exclusively: any
    /// <see cref="ContributionSeriesEntity"/> whose <c>Data</c> deserializes to a v1 payload is
    /// automatically marked as a tombstone (Version = PendingDelete) by
    /// <c>ContributionDbContext</c>, so it is removed everywhere and regenerated as v2.
    ///
    /// Do not build new records with this type.
    /// </summary>
    public class ContributionRecordV1
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("v")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("i")]
        public Guid TitleId { get; set; }
        [JsonPropertyName("t")]
        public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("s")]
        public int Status { get; set; }
        [JsonPropertyName("p")]
        public bool SeenInPopular { get; set; }
        [JsonPropertyName("e")]
        public bool SeenInLatest { get; set; }
        [JsonPropertyName("l")]
        public decimal? LastChapter { get; set; }
        [JsonPropertyName("u")]
        public DateTime? LastUpdateUTC { get; set; }

    }
}
