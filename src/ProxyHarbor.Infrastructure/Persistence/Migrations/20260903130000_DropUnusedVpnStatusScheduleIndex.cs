using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Удаляет прежний status/schedule-индекс VPN. Текущая очередь выбирает endpoint
/// только по NextCheckAt через специализированные validation-индексы, а этот индекс
/// не обслуживает ни один read-path и переписывался при каждой сетевой проверке.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260903130000_DropUnusedVpnStatusScheduleIndex")]
public sealed class DropUnusedVpnStatusScheduleIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_Status_NextCheckAt";""",
            suppressTransaction: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_VpnEndpoints_Status_NextCheckAt"
            ON "VpnEndpoints" ("Status", "NextCheckAt");
            """,
            suppressTransaction: true);
}
