using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Совмещает физический порядок VPN validation indexes с LINQ ORDER BY.
/// PostgreSQL сортирует NULL последними при ASC; прежний NULLS FIRST заставлял
/// планировщик выполнять incremental sort в каждой due-партии.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260902130000_AlignVpnValidationIndexes")]
public sealed class AlignVpnValidationIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder, """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");
        Sql(migrationBuilder, """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationNullOrder";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationOrder"
            ON "VpnEndpoints" ("NextCheckAt", "LastCheckedAt", "Id");
            """);
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_VpnEndpoints_ValidationNullOrder"
            ON "VpnEndpoints" ("LastCheckedAt", "Id")
            WHERE "NextCheckAt" IS NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder, """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationNullOrder";""");
        Sql(migrationBuilder, """DROP INDEX CONCURRENTLY IF EXISTS "IX_VpnEndpoints_ValidationOrder";""");
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

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
