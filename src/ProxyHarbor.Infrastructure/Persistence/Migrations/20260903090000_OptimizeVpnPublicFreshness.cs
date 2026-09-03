using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Ускоряет точный total публичного VPN-каталога: запрос ограничивает рабочие
/// конфигурации скользящим окном LastCheckedAt, поэтому прежний quality-index
/// для ORDER BY не мог избежать полного чтения таблицы при COUNT.
/// INCLUDE-поля сохраняют фильтры protocol/country внутри компактного индекса.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260903090000_OptimizeVpnPublicFreshness")]
public sealed class OptimizeVpnPublicFreshness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_PublicFreshness";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_PublicFreshness"
            ON "VpnEndpoints" ("LastCheckedAt")
            INCLUDE ("Protocol", "CountryCode")
            WHERE "Status" = 1
              AND "ConnectionUri" IS NOT NULL
              AND "CountryCode" IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_PublicFreshness";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
