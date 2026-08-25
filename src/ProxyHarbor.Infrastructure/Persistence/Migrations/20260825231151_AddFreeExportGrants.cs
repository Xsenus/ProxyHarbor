using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFreeExportGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FreeProxyExportGrants",
                columns: table => new
                {
                    ClientKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastGrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAllowedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FreeProxyExportGrants", x => x.ClientKey);
                    table.CheckConstraint("CK_FreeProxyExportGrants_Timeline", "\"NextAllowedAt\" > \"LastGrantedAt\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FreeProxyExportGrants_NextAllowedAt",
                table: "FreeProxyExportGrants",
                column: "NextAllowedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FreeProxyExportGrants");
        }
    }
}
