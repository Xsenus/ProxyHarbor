using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SourceFetchBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextFetchAt",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourcesSkipped",
                table: "Runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Enabled_NextFetchAt",
                table: "Sources",
                columns: new[] { "Enabled", "NextFetchAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sources_Enabled_NextFetchAt",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "NextFetchAt",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SourcesSkipped",
                table: "Runs");
        }
    }
}
