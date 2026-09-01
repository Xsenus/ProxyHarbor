using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Ограничивает холодный публичный VPN-запрос компактным partial index. Индекс
/// содержит только готовые рабочие конфигурации, совпадает с точным порядком
/// каталога и одновременно позволяет считать total без чтения всей heap-таблицы.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260902014808_OptimizeVpnPublicCatalog")]
public sealed class OptimizeVpnPublicCatalog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_PublicQuality";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_PublicQuality"
            ON "VpnEndpoints" (("LatencyMs" IS NULL), "LatencyMs", "SuccessfulChecks" DESC, "Id")
            WHERE "Status" = 1 AND "ConnectionUri" IS NOT NULL AND "CountryCode" IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_PublicQuality";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
