using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A canonical source definition in the local contributor database. Holds the
    /// immutable source identity (package + numeric source id) and the display metadata
    /// that is shared by every <see cref="ContributionSeriesEntity"/> row pointing at it.
    /// Mirrors the Cloudflare worker's <c>sources</c> table.
    /// </summary>
    public sealed class ContributionSourceEntity
    {
        /// <summary>BLOB(16) primary key (stored as a true 16-byte binary Guid).</summary>
        [JsonPropertyName("i")]
        public Guid Id { get; set; }

        /// <summary>
        /// BLOB(16) deterministic identifier of the owning PACKAGE (MD5 of the package name).
        /// Mirrors <see cref="ContributionSeriesEntity.PackageId"/> so a series entry and every
        /// source that contributes to it can be scoped to the same package without a join.
        /// </summary>
        [JsonPropertyName("pi")]
        public Guid PackageId { get; set; }

        [JsonPropertyName("p")]
        public string Package { get; init; } = string.Empty;

        [JsonPropertyName("s")]
        public long SourceId { get; init; }

        [JsonPropertyName("n")]
        public string SourceName { get; set; } = string.Empty;

        [JsonPropertyName("l")]
        public string SourceLanguage { get; set; } = string.Empty;

        [JsonPropertyName("b")]
        public DateTime? LastBatchExecutionUTC { get; set; }

        /// <summary>
        /// Ordered (most-recent first) series-entry ids of the last GetLatest run for this
        /// source. Used by the sweep incremental heuristic: page GetLatest until its ids
        /// re-overlap this set, proving no newer series were missed. Replicated alongside the
        /// source (mirrors LastBatchExecutionUTC).
        /// </summary>
        [JsonPropertyName("li")]
        public List<string> LatestIds { get; set; } = [];

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        /// <summary>
        /// Computes the stable identifier for a source from its <c>package:sourceId</c>
        /// identity (MD5 of <c>package:sourceId</c> mapped to a Guid).
        /// </summary>
        public static Guid DeriveId(string package, long sourceId)
        {
            string identity = package + ":" + sourceId.ToString(CultureInfo.InvariantCulture);
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
            return new Guid(hash);
        }

        /// <summary>
        /// Computes the stable identifier for a PACKAGE (MD5 of the package name mapped to a Guid).
        /// Stored as <see cref="PackageId"/>; the series-side twin is
        /// <see cref="ContributionSeriesEntity.DerivePackageId(string)"/>.
        /// </summary>
        public static Guid DerivePackageId(string package)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(package ?? string.Empty));
            return new Guid(hash);
        }
    }
}
