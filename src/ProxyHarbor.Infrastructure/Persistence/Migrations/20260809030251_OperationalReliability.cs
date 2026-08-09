using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationalReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "Sources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSucceededAt",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourcesFailed",
                table: "Runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SourcesSucceeded",
                table: "Runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckLeaseUntil",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Enabled_ConsecutiveFailures",
                table: "Sources",
                columns: new[] { "Enabled", "ConsecutiveFailures" });

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_CheckLeaseUntil_LastCheckedAt",
                table: "Proxies",
                columns: new[] { "CheckLeaseUntil", "LastCheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sources_Enabled_ConsecutiveFailures",
                table: "Sources");

            migrationBuilder.DropIndex(
                name: "IX_Proxies_CheckLeaseUntil_LastCheckedAt",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "LastSucceededAt",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SourcesFailed",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "SourcesSucceeded",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "CheckLeaseUntil",
                table: "Proxies");
        }
    }
}
