using RensaioBackend.Models.Database;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A metadata link entry in the local contributor database. Identity is a versioned
    /// (ProviderId, ProviderKey) link within a series, mirroring the Cloudflare worker's
    /// <c>metadata</c> table.
    /// </summary>
    public class ContributionMetadataEntity : IVersionedRow
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        [JsonPropertyName("i")]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>BLOB(16) FK → Series.Id.</summary>
        [JsonPropertyName("m")]
        public Guid SeriesId { get; set; }

        /// <summary>Numeric provider identifier (e.g. anilist/mal enum).</summary>
        [JsonPropertyName("p")]
        public int ProviderId { get; set; }

        /// <summary>Provider-specific key (e.g. AniList id).</summary>
        [JsonPropertyName("k")]
        public string? ProviderKey { get; set; }

        [JsonPropertyName("s")]
        public SeriesMappingStatus MappingStatus { get; set; } = SeriesMappingStatus.Unmatched;

        [JsonPropertyName("u")]
        public DateTime? LinkedDate { get; set; }

        /// <summary>
        /// Provider cover image URL resolved from the provider detail call. Local-only:
        /// never serialized to the cloud snapshot and not part of the protobuf export.
        /// </summary>
        [JsonIgnore]
        public string? CoverUrl { get; set; }

        /// <summary>
        /// The deterministic title ids (<see cref="TitleEntity.DeriveId(string)"/>) that the
        /// provider reported for this link (its canonical title + alternative titles). Contributor.db
        /// is the AUTHORITATIVE source for title associations, so the reconcile pass keeps exactly
        /// these titles alive for an AutoMatched / UserConfirmed row — it no longer needs to consult
        /// rensaio.db (whose SeriesMappings graph does not exist for contribution-only / sweep-created
        /// mappings). Local-only: [JsonIgnore] and absent from the protobuf export.
        /// </summary>
        [JsonIgnore]
        public List<Guid> ProviderTitleIds { get; set; } = [];

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual SeriesEntity? Series { get; set; }
    }
}