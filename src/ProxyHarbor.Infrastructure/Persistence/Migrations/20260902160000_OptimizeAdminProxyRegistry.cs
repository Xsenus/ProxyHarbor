using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Устраняет full-table sort административного proxy-реестра. INCLUDE-поля
/// позволяют отсеивать стандартные status/protocol/country-фильтры прямо при
/// упорядоченном index scan, а heap читается только для небольшой страницы.
/// Индекс является query-specific SQL-деталью и намеренно не входит в EF-модель.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260902160000_OptimizeAdminProxyRegistry")]
public sealed class OptimizeAdminProxyRegistry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Concurrent build не останавливает collector и checker-узлы на большой
        // production-таблице. DROP делает повтор безопасным после оборванного CREATE.
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_Admin_LastCheckedAt";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_Proxies_Admin_LastCheckedAt"
            ON "Proxies" (
                ("LastCheckedAt" IS NULL),
                "LastCheckedAt" DESC,
                "Id")
            INCLUDE ("Status", "Protocol", "CountryCode");
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_Admin_LastCheckedAt";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
