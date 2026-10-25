using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RensaioBackend.Migrations.Contribution
{
    /// <summary>
    /// Adds the local-only <c>Metadata.provider_title_ids</c> column.
    ///
    /// contributor.db is the AUTHORITATIVE source for title associations, so the reconcile pass
    /// keeps exactly the titles a provider reported for an AutoMatched / UserConfirmed link
    /// (its canonical + alternative titles, stored as deterministic <c>TitleEntity.DeriveId</c>
    /// Guids, JSON-encoded in this TEXT column). This removes the reconcile's dependency on
    /// rensaio.db's SeriesMappings graph, which does not exist for contribution-only /
    /// sweep-created mappings.
    ///
    /// ChangeTracker-only column: [JsonIgnore] and NOT part of the protobuf export.
    ///
    /// Existing rows start NULL/empty; the next scan / propagation / confirm / import / download
    /// pass repopulates them.
    /// </summary>
    [DbContext(typeof(ContributionDbContext))]
    [Microsoft.EntityFrameworkCore.Migrations.Migration("20260930140000_AddMetadataProviderTitleIds")]
    public partial class AddMetadataProviderTitleIds : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Metadata"" ADD COLUMN ""provider_title_ids"" TEXT NULL;");

            // Existing rows are created NULL by ADD COLUMN. The model maps this column as a
            // non-nullable JSON list (List<Guid> + HasJsonConversion), so a NULL value makes EF
            // materialization call GetString on NULL and crash ("The data is NULL at ordinal 0").
            // Normalize every pre-existing row to an empty JSON array so reads succeed; the next
            // scan / propagation / confirm pass repopulates the real ids.
            migrationBuilder.Sql(@"UPDATE ""Metadata"" SET ""provider_title_ids"" = '[]' WHERE ""provider_title_ids"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Metadata"" DROP COLUMN ""provider_title_ids"";");
        }
    }
}
