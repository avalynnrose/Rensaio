using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the per-series-entry <c>Metadata</c> flag to the local contribution database
    /// (contributor.db) plus the index that backs the two-phase sweep's candidate query.
    ///
    /// Semantics (derived denormalization — NEVER authoritative):
    ///   * <c>Metadata = true</c>  → the owning series has at least one LIVE <c>Metadata</c> row
    ///     (a real provider link or an empty-key <c>Unmatched</c> placeholder); a metadata scan
    ///     has already been attempted for it.
    ///   * <c>Metadata = false</c> → no metadata row exists yet; the series still needs a metadata
    ///     pass. Existing rows default to 0 (false), which is the safe state: the next two-phase
    ///     sweep (or <c>ContributionGraphRepairService.RecomputeSeriesMetadataFlagsAsync</c>) will
    ///     process them.
    ///
    /// The value is recomputable at any time from the <c>SeriesEntries</c> → <c>Metadata</c>
    /// relationship; <c>true</c> does NOT mean "matched" (that still requires an
    /// AutoMatched/UserConfirmed <c>MappingStatus</c>). Drift is corrected by the repair pass.
    ///
    /// Index added:
    ///   * <c>IX_SeriesEntries_Metadata</c> — SeriesEntries(Metadata); the two-phase sweep's hot
    ///     query enumerates entries with <c>Metadata = 0</c>, so a Metadata-led index keeps that
    ///     scan from touching the table row.
    ///
    /// Fresh databases never run this migration: they are created from the model via
    /// EnsureCreated (which emits the column + index) and every migration is marked applied.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261012000000_AddSeriesMetadataFlag")]
    public partial class AddSeriesMetadataFlag : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT NULL with a DEFAULT so pre-existing rows materialize as false (safe: still needs
            // a metadata pass). A fresh schema is created from the model and marks migrations
            // applied, so this only runs on databases predating the column.
            migrationBuilder.Sql(
                @"ALTER TABLE ""SeriesEntries"" ADD COLUMN ""Metadata"" INTEGER NOT NULL DEFAULT 0;");

            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ""IX_SeriesEntries_Metadata"" ON ""SeriesEntries"" (""Metadata"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_SeriesEntries_Metadata"";");
            // SQLite cannot DROP a column without a table rebuild; the column is left in place
            // on downgrade (harmless — it is a non-null INTEGER that older code simply ignores).
        }
    }
}
