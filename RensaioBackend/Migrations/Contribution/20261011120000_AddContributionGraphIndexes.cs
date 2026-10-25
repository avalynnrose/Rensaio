using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the second round of contribution database (contributor.db) performance indexes,
    /// targeting the CONFIRMED hot scans on a large graph (≈360k SeriesTitles / ≈300k Metadata).
    ///
    /// Every hot path filters on the replication <c>Version</c> set (LIVE = not -1/-3) and, on a
    /// grown database, the majority of SeriesTitles rows are tombstones (-1), so index shape is
    /// what decides whether a query seeks or scans:
    ///
    ///   * per-series reads (`series_id = ? AND version live`) — reconcile, mapping detail,
    ///     merge/split. Before: the series_id-only lookups read EVERY title row of the series,
    ///     tombstones included.
    ///   * title resolution (`title_id IN (...) AND version live`) — sweep, propagation, link
    ///     engine. Before: a non-covering title_id FM index then a table row fetch per title.
    ///   * graph-wide scans bound only on Version — the reconcile's DISTINCT live series, the
    ///     grouping reconcile + cycle-guard signature on SeriesEntries.
    ///
    /// Indexes added:
    ///   * <c>IX_SeriesTitles_series_id_version</c>              — SeriesTitles(series_id, version)
    ///   * <c>IX_SeriesTitles_version_series_id</c>              — SeriesTitles(version, series_id)
    ///   * <c>IX_SeriesTitles_title_id_series_id_version</c>     — SeriesTitles(title_id, series_id, version)
    ///   * <c>IX_SeriesEntries_Version</c>                       — SeriesEntries(Version)
    ///   * <c>IX_Metadata_series_id_MappingStatus_Version</c>    — Metadata(series_id, MappingStatus, Version)
    ///   * <c>IX_Metadata_MappingStatus_Version</c>              — Metadata(MappingStatus, Version)
    ///
    /// Superseded indexes dropped in the same migration:
    ///   * <c>IX_SeriesTitles_version</c>        (replaced by the Version-led composite, which is a
    ///     strict prefix superset and lets DISTINCT series_id enumerate from the index).
    ///   * <c>IX_SeriesTitles_title_id</c>       (replaced by the title_id-led composite).
    ///   * <c>IX_Metadata_series_id_MappingStatus</c> (replaced by the Version-extended composite).
    ///
    /// Fresh databases never run this migration: they are created from the model via EnsureCreated
    /// (which emits these indexes via OnModelCreating) and every migration is marked applied.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261011120000_AddContributionGraphIndexes")]
    public partial class AddContributionGraphIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── New SeriesTitles indexes ──
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_series_id_version"" ON ""SeriesTitles"" (""series_id"", ""version"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_version_series_id"" ON ""SeriesTitles"" (""version"", ""series_id"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_title_id_series_id_version"" ON ""SeriesTitles"" (""title_id"", ""series_id"", ""version"");");

            // Drop the superseded single-column indexes. The Version-led composite is a strict
            // prefix superset of (version); the title_id-led composite supersedes (title_id).
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_title_id"";");

            // ── SeriesEntries: the grouping reconcile / signature full scan on Version ──
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_Version"" ON ""SeriesEntries"" (""Version"");");

            // ── Metadata: covering per-mapping and graph-wide status+version scans ──
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_series_id_MappingStatus_Version"" ON ""Metadata"" (""series_id"", ""MappingStatus"", ""Version"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_MappingStatus_Version"" ON ""Metadata"" (""MappingStatus"", ""Version"");");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_series_id_MappingStatus"";");

            // Refresh planner statistics so the new indexes are chosen immediately (otherwise the
            // query planner falls back to a full scan until it has sampled the changed indexes).
            migrationBuilder.Sql(@"ANALYZE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the superseded indexes.
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_version"" ON ""SeriesTitles"" (""version"");");
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_title_id"" ON ""SeriesTitles"" (""title_id"");");
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_series_id_MappingStatus"" ON ""Metadata"" (""series_id"", ""MappingStatus"");");

            // Drop the new indexes.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_series_id_version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_version_series_id"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_title_id_series_id_version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_Version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_series_id_MappingStatus_Version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_MappingStatus_Version"";");
        }
    }
}
