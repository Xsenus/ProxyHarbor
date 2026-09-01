using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Разделяет обычную validation-очередь и редкие просроченные аренды. Частичный
/// индекс свободных строк больше не проходит через тысячи активных lease, а
/// компактный индекс срока аренды обеспечивает быстрый failover потерянной VPS.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260902022000_OptimizeValidationLeaseClaims")]
public sealed class OptimizeValidationLeaseClaims : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ValidationClaimUnleased"
            ON "Proxies" (
                (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                "NextCheckAt" NULLS FIRST,
                "LastCheckedAt" NULLS FIRST)
            WHERE "CheckLeaseUntil" IS NULL;
            """);
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ExpiredLeaseClaim"
            ON "Proxies" ("CheckLeaseUntil")
            WHERE "CheckLeaseUntil" IS NOT NULL;
            """);
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationClaimOrder";""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ValidationClaimOrder"
            ON "Proxies" (
                (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                "NextCheckAt" NULLS FIRST,
                "LastCheckedAt" NULLS FIRST);
            """);
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationClaimUnleased";""");
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ExpiredLeaseClaim";""");
    }

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
