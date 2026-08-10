using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Добавляет точный порядок validation claim без блокирующей сортировки всей due-очереди.
/// Индекс является operational SQL-деталью и намеренно не входит в EF-модель сущности.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260810234000_OptimizeValidationClaimOrder")]
public sealed class OptimizeValidationClaimOrder : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Concurrent build сохраняет доступность collector/validator на большой production-БД.
        // Предварительный DROP позволяет безопасно повторить migration после оборванного CREATE,
        // которое PostgreSQL могло оставить как invalid index с занятым именем.
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationClaimOrder";""");
        Sql(migrationBuilder,
            """
            CREATE INDEX CONCURRENTLY "IX_Proxies_ValidationClaimOrder"
            ON "Proxies" (
                (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                "NextCheckAt" NULLS FIRST,
                "LastCheckedAt" NULLS FIRST);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        Sql(migrationBuilder,
            """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationClaimOrder";""");

    private static void Sql(MigrationBuilder migrationBuilder, string command) =>
        migrationBuilder.Sql(command, suppressTransaction: true);
}
