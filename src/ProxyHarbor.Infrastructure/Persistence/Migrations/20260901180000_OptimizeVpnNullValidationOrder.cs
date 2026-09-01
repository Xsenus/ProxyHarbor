using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Делает обе ветви VPN validation dequeue полностью bounded. Отдельный partial
/// index не заставляет PostgreSQL читать и сортировать весь never-checked каталог,
/// а Id задаёт воспроизводимый порядок строк с одинаковым временем проверки.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260901180000_OptimizeVpnNullValidationOrder")]
public sealed class OptimizeVpnNullValidationOrder : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationNullOrder";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationOrder"
            ON "VpnEndpoints" ("NextCheckAt", "LastCheckedAt" NULLS FIRST, "Id");
            """);
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationNullOrder"
            ON "VpnEndpoints" ("LastCheckedAt" NULLS FIRST, "Id")
            WHERE "NextCheckAt" IS NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationNullOrder";""");
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationOrder"
            ON "VpnEndpoints" ("NextCheckAt", "LastCheckedAt" NULLS FIRST);
            """);
    }

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
