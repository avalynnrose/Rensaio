using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the covering indexes that accelerate the metadata-flag recompute on a large graph.
    ///
    /// <c>ContributionGraphRepairService.RecomputeSeriesMetadataFlagsAsync</c> derives the DERIVED
    /// <c>SeriesEntries.Metadata</c> flag from the graph. Its two predicates are:
    ///   * <c>SeriesEntries.Metadata = ? AND Version live</c> — the set of series that must flip
    ///     between covered / uncovered;
    ///   * <c>Metadata.Version live</c> — the set of series that already carry a live metadata row.
    ///
    /// Before this migration both probes filtered on <c>Version</c> alone, so on a large database
    /// (where most rows are tombstones) the recompute walked the whole table each time it ran. It
    /// runs after EVERY merge/split (the two-phase sweep + each join/split settle), so its cost was
    /// paid dozens of times per import — the dominant per-operation latency in the sweep logs.
    ///
    /// Indexes added:
    ///   * <c>IX_SeriesEntries_Metadata_Version</c>  — SeriesEntries(Metadata, Version)
    ///   * <c>IX_Metadata_Version_series_id</c>      — Metadata(Version, series_id)
    ///
    /// Superseded index dropped in the same migration:
    ///   * <c>IX_Metadata_Version</c> — the (Version, series_id) composite is a strict prefix
    ///     superset and lets the DISTINCT series_id enumerate straight from the index.
    ///
    /// The per-merge/split settle also SCOPES its probe to the touched series ids, so a join no
    /// longer re-reads the whole Metadata table just to settle a handful of entries.
    ///
    /// Fresh databases never run this migration: they are created from the model via EnsureCreated
    /// (which emits these indexes via OnModelCreating) and every migration is marked applied.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261022060000_AddMetadataFlagRecomputeIndexes")]
    public partial class AddMetadataFlagRecomputeIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SeriesEntries: the recompute's "which series must flip" probe filters
            // `Metadata = ? AND Version live`; the composite evaluates both in the index.
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_Metadata_Version"" ON ""SeriesEntries"" (""Metadata"", ""Version"");");

            // Metadata: the recompute's "which series are covered" probe enumerates DISTINCT live
            // series_id; the Version-led composite is a covering index scan and supersedes the
            // single-column IX_Metadata_Version (a strict prefix superset).
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_Version_series_id"" ON ""Metadata"" (""Version"", ""series_id"");");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_Version"";");

            // Refresh planner statistics so the new indexes are chosen immediately.
            migrationBuilder.Sql(@"ANALYZE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the superseded single-column index.
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_Version"" ON ""Metadata"" (""Version"");");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_Metadata_Version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_Version_series_id"";");
        }
    }
}
