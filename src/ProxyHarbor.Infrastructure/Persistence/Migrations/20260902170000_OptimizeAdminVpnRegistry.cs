using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Даёт административному VPN-каталогу физический порядок его основного
/// lastChecked-сортирования. INCLUDE-поля позволяют PostgreSQL отбрасывать
/// обычные фильтры внутри индекса до чтения полной строки endpoint.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260902170000_OptimizeAdminVpnRegistry")]
public sealed class OptimizeAdminVpnRegistry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_Admin_LastCheckedAt";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_Admin_LastCheckedAt"
            ON "VpnEndpoints" (("LastCheckedAt" IS NULL), "LastCheckedAt" DESC, "Id")
            INCLUDE ("Status", "Protocol", "Transport", "CountryCode");
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_Admin_LastCheckedAt";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
