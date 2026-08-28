using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVpnSourceFetchBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextFetchAt",
                table: "VpnSources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VpnSources_Enabled_NextFetchAt",
                table: "VpnSources",
                columns: new[] { "Enabled", "NextFetchAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSources_FetchTimeline",
                table: "VpnSources",
                sql: "\"LastSucceededAt\" IS NULL OR (\"LastFetchedAt\" IS NOT NULL AND \"LastSucceededAt\" <= \"LastFetchedAt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VpnSources_Enabled_NextFetchAt",
                table: "VpnSources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSources_FetchTimeline",
                table: "VpnSources");

            migrationBuilder.DropColumn(
                name: "NextFetchAt",
                table: "VpnSources");
        }
    }
}
