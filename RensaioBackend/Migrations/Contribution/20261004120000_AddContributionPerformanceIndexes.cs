using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds indexes that accelerate the contribution database (contributor.db) flows as the
    /// graph grows.
    ///
    /// Every hot path filters on the replication <c>Version</c> set (LIVE = not -1/-3):
    ///   * the Contribution page (<see cref="ContributionMappingService.ListMappingsAsync"/>)
    ///     loads SeriesTitles / SeriesEntries / Metadata filtered only on Version;
    ///   * per-series reads and mutations (`series_id = ? AND Version NOT IN (-1,-3)`) drive the
    ///     scan / block / unblock / ignore / join / split actions;
    ///   * the upload sweep and the graph-wide consolidate / grouping reconcile scan the whole
    ///     table on Version alone.
    ///
    /// Indexes added:
    ///   * <c>IX_SeriesTitles_version</c>                 — SeriesTitles(version)
    ///   * <c>IX_SeriesEntries_series_id_Version</c>      — SeriesEntries(series_id, Version)
    ///   * <c>IX_Metadata_series_id_Version</c>           — Metadata(series_id, Version)
    ///   * <c>IX_Metadata_Version</c>                     — Metadata(Version)
    ///
    /// series_id / title_id are already covered by the table primary keys and the existing FK
    /// indexes (<c>IX_SeriesEntries_series_id</c>, <c>IX_Metadata_series_id</c>, SeriesTitles.TitleId).
    /// Fresh databases never run this migration: they are created from the model via EnsureCreated
    /// (which emits these indexes) and every migration is marked applied.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261004120000_AddContributionPerformanceIndexes")]
    public partial class AddContributionPerformanceIndexes : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS keeps the migration idempotent when a schema was already created from
            // a model that carried the index (defensive; EnsureCreated paths mark migrations applied).
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesTitles_version"" ON ""SeriesTitles"" (""version"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_series_id_Version"" ON ""SeriesEntries"" (""series_id"", ""Version"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_series_id_Version"" ON ""Metadata"" (""series_id"", ""Version"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_Version"" ON ""Metadata"" (""Version"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesTitles_version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_series_id_Version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_series_id_Version"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_Version"";");
        }
    }
}
