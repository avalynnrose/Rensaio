using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// PART 1 (additive) of the SourceSeries normalization.
    ///
    /// Adds the package-scoped identity columns (<c>Sources.PackageId</c>,
    /// <c>SeriesEntries.PackageId</c>) and the many-to-many <c>SourceSeries</c> association table.
    /// Purely additive so the application keeps working mid-rollout; the existing
    /// <c>SeriesEntries.SourceId</c> FK is intentionally left in place and is dropped by a later
    /// migration (see <c>DropSeriesSourceId</c>).
    ///
    /// The data backfill (derive PackageId, consolidate same-package same-url entries onto one
    /// SeriesEntries row, populate SourceSeries) is performed by the app-level
    /// <c>ContributionSourceSeriesMigrator</c> at startup, because grouping by the JSON
    /// <c>Data.Url</c> cannot be expressed reliably in SQL.
    /// </summary>
    public partial class AddSourceSeriesAndPackageId : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder)
        {
            // 16 zero bytes == Guid.Empty, which the strict GuidToBlob16Converter requires. A
            // 0-length default (the additive-migration bug) makes every pre-existing row throw
            // "Guid BLOB must be exactly 16 bytes" on read.
            migrationBuilder.AddColumn<byte[]>(
                name: "PackageId",
                table: "Sources",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[16]);

            migrationBuilder.AddColumn<byte[]>(
                name: "PackageId",
                table: "SeriesEntries",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[16]);

            migrationBuilder.CreateTable(
                name: "SourceSeries",
                columns: table => new
                {
                    source_id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    series_entity_id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSeries", x => new { x.source_id, x.series_entity_id });
                    table.ForeignKey(
                        name: "FK_SourceSeries_SeriesEntries_series_entity_id",
                        column: x => x.series_entity_id,
                        principalTable: "SeriesEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceSeries_Sources_source_id",
                        column: x => x.source_id,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesEntries_PackageId",
                table: "SeriesEntries",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSeries_series_entity_id_Version",
                table: "SourceSeries",
                columns: new[] { "series_entity_id", "version" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSeries_source_id_Version",
                table: "SourceSeries",
                columns: new[] { "source_id", "version" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSeries_Version",
                table: "SourceSeries",
                column: "version");
        }

        /// <inheritdoc />
        protected override void Down(Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SourceSeries");

            migrationBuilder.DropIndex(
                name: "IX_SeriesEntries_PackageId",
                table: "SeriesEntries");

            migrationBuilder.DropColumn(name: "PackageId", table: "Sources");
            migrationBuilder.DropColumn(name: "PackageId", table: "SeriesEntries");
        }
    }
}
