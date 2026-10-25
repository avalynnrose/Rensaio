using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the per-series-entry <c>VerifiedDateUTC</c> verification flag to the local
    /// contribution database (contributor.db) and the indexes that back the new Contribution
    /// page flows/filters/views.
    ///
    /// Semantics:
    ///   * <c>VerifiedDateUTC IS NULL</c>        → the source entry is Unverified.
    ///   * <c>VerifiedDateUTC</c> with a value   → the source entry is Verified.
    ///   * A mapping is Verified only when EVERY live entry carries a date; any null live entry
    ///     makes the mapping Unverified. The page derives this per mapping from its live entries.
    ///
    /// Indexes added:
    ///   * <c>IX_SeriesEntries_series_id_VerifiedDateUTC</c> — SeriesEntries(series_id, VerifiedDateUTC)
    ///   * <c>IX_SeriesEntries_VerifiedDateUTC</c>           — SeriesEntries(VerifiedDateUTC)
    ///   * <c>IX_Metadata_series_id_MappingStatus</c>        — Metadata(series_id, MappingStatus)
    ///
    /// The first composite backs the per-mapping Verified/Unverified EXISTS/NOT-EXISTS checks;
    /// the single-column index backs the graph-wide verified/unverified scan; the metadata
    /// composite backs the Unmatched / Blocked / ManualIgnored combos (presence of a live
    /// metadata row of that status for a mapping).
    ///
    /// Fresh databases never run this migration: they are created from the model via
    /// EnsureCreated (which emits the column + indexes) and every migration is marked applied.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261005120000_AddSeriesVerifiedDate")]
    public partial class AddSeriesVerifiedDate : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable TEXT column; NULL = unverified. Plain ALTER is safe here because a fresh
            // schema is created from the model (and marks migrations applied), so this only runs
            // on databases predating the column.
            migrationBuilder.Sql(
                @"ALTER TABLE ""SeriesEntries"" ADD COLUMN ""VerifiedDateUTC"" TEXT;");

            // IF NOT EXISTS keeps the migration idempotent when a schema was already created from
            // a model that carried the indexes (defensive; EnsureCreated paths mark migrations applied).
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_series_id_VerifiedDateUTC"" ON ""SeriesEntries"" (""series_id"", ""VerifiedDateUTC"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_VerifiedDateUTC"" ON ""SeriesEntries"" (""VerifiedDateUTC"");");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_Metadata_series_id_MappingStatus"" ON ""Metadata"" (""series_id"", ""MappingStatus"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_series_id_VerifiedDateUTC"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_VerifiedDateUTC"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Metadata_series_id_MappingStatus"";");
            // SQLite cannot DROP a column without a table rebuild; the column is left in place
            // on downgrade (harmless — it is nullable and simply ignored by older code).
        }
    }
}
