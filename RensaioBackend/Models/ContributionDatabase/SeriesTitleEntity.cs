using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// Join row linking a <see cref="SeriesEntity"/> to a <see cref="TitleEntity"/>.
    /// Composite primary key (SeriesId, TitleId).
    /// </summary>
    public class SeriesTitleEntity : IVersionedRow
    {
        /// <summary>BLOB(16) FK → Series.Id.</summary>
        [JsonPropertyName("m")]
        public Guid SeriesId { get; set; }

        /// <summary>BLOB(16) FK → Titles.Id.</summary>
        [JsonPropertyName("t")]
        public Guid TitleId { get; set; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual SeriesEntity? Series { get; set; }
        public virtual TitleEntity? Title { get; set; }
    }
}