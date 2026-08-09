using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ValidationAttemptTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastValidationAttemptAt",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastValidationDeferred",
                table: "Proxies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Исторические полноценные проверки являются завершёнными попытками; backfill
            // не помечает их Deferred и сохраняет непрерывность operational telemetry.
            migrationBuilder.Sql("""
                UPDATE "Proxies"
                SET "LastValidationAttemptAt" = "LastCheckedAt"
                WHERE "LastCheckedAt" IS NOT NULL
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_LastValidationAttemptAt",
                table: "Proxies",
                column: "LastValidationAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Proxies_LastValidationAttemptAt",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "LastValidationAttemptAt",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "LastValidationDeferred",
                table: "Proxies");
        }
    }
}
