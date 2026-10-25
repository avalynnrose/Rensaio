using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.ContributionDatabase
{
    /// <summary>
    /// A source entry in the local contributor database. <c>Id</c> is the
    /// contributor-provided identifier (TEXT), not a UUID, mirroring the Cloudflare
    /// worker's <c>sources</c> table. The canonical source metadata is stored on the
    /// related <see cref="ContributionSourceEntity"/> via <see cref="SourceId"/>.
    /// </summary>
    public class ContributionSeriesEntity : IVersionedRow
    {
        /// <summary>Contributor-provided identifier (MD5 -> Bytes).</summary>
        ///
        [JsonPropertyName("i")]
        public Guid Id { get; set; }

        /// <summary>BLOB(16) FK → Series.Id.</summary>
        [JsonPropertyName("m")]
        public Guid SeriesId { get; set; }

        /// <summary>
        /// BLOB(16) deterministic identifier of the owning PACKAGE (MD5 of the package name).
        /// Series-entry identity is package-scoped (<see cref="DeriveId(string, string)"/>), so
        /// two identical urls from DIFFERENT packages remain distinct rows.
        /// </summary>
        [JsonPropertyName("pi")]
        public Guid PackageId { get; set; }

        /// <summary>
        /// BLOB(16) FK → ContributionSourceEntity.Id.
        ///
        /// LEGACY / representative source: retained for wire + cloud compatibility. The
        /// authoritative source relationship is the many-to-many <see cref="Sources"/> set
        /// (<see cref="ContributionSourceSeriesEntity"/>), which allows one series entry to be
        /// shared by several sources of the same package. This column is removed by the final
        /// migration step.
        /// </summary>
        [JsonPropertyName("s")]
        public Guid SourceId { get; set; }

        [JsonPropertyName("d")]
        /// <summary>
        /// Serialized source data (TEXT). Always the CURRENT schema (<see cref="ContributionRecordV2"/>);
        /// a legacy v1 payload materialized here has <see cref="ContributionRecordV2.IsLegacy"/> set
        /// and causes the owning row to be tombstoned on save (see <c>ContributionDbContext</c>).
        /// </summary>
        public ContributionRecordV2 Data { get; set; }

        /// <summary>
        /// UTC timestamp at which this source entry was manually verified on the Contribution page
        /// (null = unverified). A mapping is Verified only when EVERY live entry carries a value;
        /// any null live entry makes the mapping Unverified. The value is one-way toward the cloud:
        /// a non-null value propagates, a local clear (Un-Verify / Join / Split) never re-uploads.
        /// </summary>
        [JsonPropertyName("z")]
        public DateTime? VerifiedDateUTC { get; set; }

        /// <summary>
        /// Whether the owning <see cref="SeriesEntity"/> already carries at least one LIVE
        /// <see cref="ContributionMetadataEntity"/> row (any <c>MappingStatus</c>, including an
        /// empty-key <c>Unmatched</c> placeholder). This is a DERIVED convenience denormalization
        /// of the <c>SeriesEntries</c> → <c>Metadata</c> relationship — never authoritative.
        ///
        /// Semantics:
        ///   * <c>true</c>  → a metadata scan has already been attempted for this series (at least
        ///     one provider owns a live row: a real link or an Unmatched placeholder).
        ///   * <c>false</c> → no metadata row exists yet; the series still needs a metadata pass.
        ///
        /// IMPORTANT: <c>true</c> does NOT mean "matched" — it only means "a metadata row exists".
        /// Any gating that needs an active link must still check <c>MappingStatus</c>
        /// (AutoMatched / UserConfirmed). The value is recomputable at any time from the graph
        /// (see <c>ContributionGraphRepairService.RecomputeSeriesMetadataFlagsAsync</c>); writer
        /// paths may set it best-effort and drift is corrected by the repair pass. It must fail
        /// toward <c>false</c> whenever the graph is restructured (join / split / reparent /
        /// metadata delete) so a series that lost its metadata is re-scanned.
        /// </summary>
        [JsonPropertyName("q")]
        public bool Metadata { get; set; }

        /// <summary>Replication version counter.</summary>
        [JsonPropertyName("v")]
        public int Version { get; set; }

        public virtual SeriesEntity? Series { get; set; }

        public virtual ContributionSourceEntity? Source { get; set; }

        /// <summary>
        /// The many-to-many source associations for this series entry. One series entry may be
        /// contributed by several sources of the SAME package that expose the same <c>Data.Url</c>.
        /// </summary>
        public virtual ICollection<ContributionSourceSeriesEntity> Sources { get; set; }
            = new List<ContributionSourceSeriesEntity>();

        /// <summary>
        /// Computes the stable identifier for a series entry from its <c>package:url</c>
        /// identity (MD5 of <c>package:url</c> mapped to a Guid). Source-independent: the numeric
        /// source id is deliberately NOT part of the identity so multiple sources of one package
        /// that expose the same url collapse onto ONE series entry (see <c>SourceSeries</c>).
        /// </summary>
        public static Guid DeriveId(string package, string url)
        {
            string identity = package + ":" + url.ToLowerInvariant();
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
            return new Guid(hash);
        }

        /// <summary>
        /// Computes the stable identifier for a PACKAGE (MD5 of the package name mapped to a Guid).
        /// Twin of <see cref="ContributionSourceEntity.DerivePackageId(string)"/>.
        /// </summary>
        public static Guid DerivePackageId(string package)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(package ?? string.Empty));
            return new Guid(hash);
        }

    }
}

