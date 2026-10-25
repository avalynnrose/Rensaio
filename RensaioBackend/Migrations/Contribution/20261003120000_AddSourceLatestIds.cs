using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the <c>Sources.LatestIds</c> column: the ordered (most-recent first) series-entry ids
    /// of the last GetLatest run for a source, JSON-encoded in a TEXT column (mirrors
    /// <c>Metadata.provider_title_ids</c>).
    ///
    /// Used by the sweep's incremental heuristic: when a source supports GetLatest and has already
    /// been swept, page GetLatest until its ids re-overlap this set (allowing up to 3 misses),
    /// proving no newer series were missed — avoiding a full GetPopular pass.
    ///
    /// Replicated alongside the source (part of the protobuf export), unlike the local-only
    /// <c>provider_title_ids</c>.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20261003120000_AddSourceLatestIds")]
    public partial class AddSourceLatestIds : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Sources"" ADD COLUMN ""LatestIds"" TEXT NULL;");

            // Existing rows are created NULL by ADD COLUMN. The model maps this column as a
            // non-nullable JSON list (List<string> + HasJsonConversion), so a NULL value makes EF
            // materialization call GetString on NULL and crash. Normalize pre-existing rows to an
            // empty JSON array; the next sweep repopulates them.
            migrationBuilder.Sql(@"UPDATE ""Sources"" SET ""LatestIds"" = '[]' WHERE ""LatestIds"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Sources"" DROP COLUMN ""LatestIds"";");
        }
    }
}
