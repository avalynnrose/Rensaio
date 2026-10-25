using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RensaioBackend.Data;
using System;

#nullable disable

namespace RensaioBackend.Migrations.Rensaio
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261005120000_AddImageHashCache")]
    public partial class AddImageHashCache : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageHashes",
                columns: table => new
                {
                    Url = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    Hash = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUTC = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageHashes", x => x.Url);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageHashes_Url",
                table: "ImageHashes",
                column: "Url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageHashes");
        }
    }
}
