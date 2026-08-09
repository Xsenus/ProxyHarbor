using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdaptiveValidationSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Proxies_CheckLeaseUntil_LastCheckedAt",
                table: "Proxies");

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailedChecks",
                table: "Proxies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCheckAt",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            // После развёртывания не выпускаем десятки тысяч старых записей в сеть одновременно:
            // pending остаются немедленными, остальные детерминированно распределяются на 15 минут.
            migrationBuilder.Sql("""
                UPDATE "Proxies"
                SET "NextCheckAt" = CASE
                    WHEN "Status" = 0 THEN NULL
                    ELSE NOW() + make_interval(secs => (((hashtext("Id"::text)::bigint + 2147483648) % 900)::integer))
                END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_NextCheckAt_CheckLeaseUntil",
                table: "Proxies",
                columns: new[] { "NextCheckAt", "CheckLeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Proxies_NextCheckAt_CheckLeaseUntil",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailedChecks",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "NextCheckAt",
                table: "Proxies");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_CheckLeaseUntil_LastCheckedAt",
                table: "Proxies",
                columns: new[] { "CheckLeaseUntil", "LastCheckedAt" });
        }
    }
}
