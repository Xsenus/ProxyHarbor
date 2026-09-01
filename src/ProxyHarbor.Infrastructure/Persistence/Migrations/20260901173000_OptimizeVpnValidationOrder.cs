using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Добавляет точный порядок VPN validation dequeue без parallel seq scan и
/// сортировки всего каталога на каждом коротком validation-цикле.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260901173000_OptimizeVpnValidationOrder")]
public sealed class OptimizeVpnValidationOrder : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationOrder"
            ON "VpnEndpoints" ("NextCheckAt", "LastCheckedAt" NULLS FIRST);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
