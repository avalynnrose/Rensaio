using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Renames the mapping-centric physical schema to the intent-revealing series names and
    /// adds the local-only Metadata.cover_url column — WITHOUT data loss.
    ///
    /// Old physical schema (created by EnsureCreated from the previous model):
    ///   Mappings        (Id)
    ///   MappingTitles   (MappingId, TitleId, Version)
    ///   Series          (Id, MappingId, SourceId, Data, Version)   ← per-source entry
    ///   Metadata        (..., MappingId, ...)
    ///
    /// New physical schema:
    ///   Series          (Id)                                       ← aggregate root (was Mappings)
    ///   SeriesTitles    (series_id, title_id, version)             ← was MappingTitles
    ///   SeriesEntries   (Id, series_id, SourceId, Data, Version)   ← was Series
    ///   Metadata        (..., series_id, cover_url, ...)
    ///
    /// The rename order matters: the old `Series` table frees the `Series` name before
    /// `Mappings` claims it. Raw SQL is used because SQLite stores FK clauses in the table
    /// DDL and EF's granular ops cannot express these renames safely on an existing DB.
    ///
    /// Fresh databases never run this migration: they are created from the model via
    /// EnsureCreated and every migration is marked applied (see StartupHostedService).
    /// </summary>
    public partial class RenameMappingsToSeriesAndAddCoverUrl : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Metadata: series_id rename + cover_url + index rename.
            migrationBuilder.Sql(@"
ALTER TABLE ""Metadata"" RENAME COLUMN ""MappingId"" TO ""series_id"";
ALTER TABLE ""Metadata"" ADD COLUMN ""cover_url"" TEXT NULL;
DROP INDEX IF EXISTS ""IX_Metadata_MappingId"";
CREATE INDEX ""IX_Metadata_series_id"" ON ""Metadata"" (""series_id"");
");

            // 2. Old Series (per-source entry) -> SeriesEntries, freeing the `Series` name.
            migrationBuilder.Sql(@"
ALTER TABLE ""Series"" RENAME TO ""SeriesEntries"";
ALTER TABLE ""SeriesEntries"" RENAME COLUMN ""MappingId"" TO ""series_id"";
DROP INDEX IF EXISTS ""IX_Sources_MappingId"";
DROP INDEX IF EXISTS ""IX_Series_SourceId"";
CREATE INDEX ""IX_SeriesEntries_series_id"" ON ""SeriesEntries"" (""series_id"");
CREATE INDEX ""IX_SeriesEntries_SourceId"" ON ""SeriesEntries"" (""SourceId"");
");

            // 3. MappingTitles -> SeriesTitles (series_id/title_id/version).
            migrationBuilder.Sql(@"
ALTER TABLE ""MappingTitles"" RENAME TO ""SeriesTitles"";
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""MappingId"" TO ""series_id"";
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""TitleId"" TO ""title_id"";
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""Version"" TO ""version"";
DROP INDEX IF EXISTS ""IX_MappingTitles_TitleId"";
CREATE INDEX ""IX_SeriesTitles_title_id"" ON ""SeriesTitles"" (""title_id"");
");

            // 4. Mappings -> Series (the new aggregate root). SQLite rewrites the FKs in the
            //    child tables (Metadata/SeriesEntries/SeriesTitles) that referenced `Mappings`.
            migrationBuilder.Sql(@"
ALTER TABLE ""Mappings"" RENAME TO ""Series"";
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""Series"" RENAME TO ""Mappings"";
");

            migrationBuilder.Sql(@"
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""series_id"" TO ""MappingId"";
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""title_id"" TO ""TitleId"";
ALTER TABLE ""SeriesTitles"" RENAME COLUMN ""version"" TO ""Version"";
DROP INDEX IF EXISTS ""IX_SeriesTitles_title_id"";
CREATE INDEX ""IX_MappingTitles_TitleId"" ON ""SeriesTitles"" (""TitleId"");
ALTER TABLE ""SeriesTitles"" RENAME TO ""MappingTitles"";
");

            migrationBuilder.Sql(@"
ALTER TABLE ""SeriesEntries"" RENAME COLUMN ""series_id"" TO ""MappingId"";
DROP INDEX IF EXISTS ""IX_SeriesEntries_series_id"";
DROP INDEX IF EXISTS ""IX_SeriesEntries_SourceId"";
CREATE INDEX ""IX_Sources_MappingId"" ON ""SeriesEntries"" (""MappingId"");
CREATE INDEX ""IX_Series_SourceId"" ON ""SeriesEntries"" (""SourceId"");
ALTER TABLE ""SeriesEntries"" RENAME TO ""Series"";
");

            migrationBuilder.Sql(@"
DROP INDEX IF EXISTS ""IX_Metadata_series_id"";
CREATE INDEX ""IX_Metadata_MappingId"" ON ""Metadata"" (""series_id"");
ALTER TABLE ""Metadata"" DROP COLUMN ""cover_url"";
ALTER TABLE ""Metadata"" RENAME COLUMN ""series_id"" TO ""MappingId"";
");
        }
    }
}
