using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateProxyValidationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxyValidationLeases",
                columns: table => new
                {
                    ProxyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyValidationLeases", x => x.ProxyId);
                    table.ForeignKey(
                        name: "FK_ProxyValidationLeases_Proxies_ProxyId",
                        column: x => x.ProxyId,
                        principalTable: "Proxies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("Npgsql:UnloggedTable", true);

            // Таблица постоянно получает INSERT/DELETE и heartbeat-UPDATE. Ранний
            // autovacuum ограничивает bloat узкой очереди независимо от размера
            // основного каталога, а fillfactor оставляет место для обновлений TTL.
            migrationBuilder.Sql("""
                ALTER TABLE "ProxyValidationLeases" SET (
                    fillfactor = 90,
                    autovacuum_vacuum_scale_factor = 0.02,
                    autovacuum_vacuum_threshold = 500,
                    autovacuum_analyze_scale_factor = 0.02,
                    autovacuum_analyze_threshold = 250);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ProxyValidationLeases_LeaseId",
                table: "ProxyValidationLeases",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyValidationLeases_LeaseUntil",
                table: "ProxyValidationLeases",
                column: "LeaseUntil");

            // Сохраняем ownership уже выданных партий при управляемой замене версии,
            // а затем очищаем прежние поля. Новый API не теряет результаты работающих
            // checker-узлов и не ждёт истечения старого TTL.
            migrationBuilder.Sql("""
                INSERT INTO "ProxyValidationLeases" ("ProxyId", "LeaseId", "LeaseUntil")
                SELECT "Id", "CheckLeaseId", "CheckLeaseUntil"
                FROM "Proxies"
                WHERE "CheckLeaseId" IS NOT NULL AND "CheckLeaseUntil" IS NOT NULL
                ON CONFLICT ("ProxyId") DO UPDATE
                SET "LeaseId" = EXCLUDED."LeaseId", "LeaseUntil" = EXCLUDED."LeaseUntil";

                UPDATE "Proxies"
                SET "CheckLeaseId" = NULL, "CheckLeaseUntil" = NULL
                WHERE "CheckLeaseId" IS NOT NULL OR "CheckLeaseUntil" IS NOT NULL;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Proxies_CheckLeaseId",
                table: "Proxies");

            migrationBuilder.DropIndex(
                name: "IX_Proxies_NextCheckAt_CheckLeaseUntil",
                table: "Proxies");

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ValidationQueueOrder"
                ON "Proxies" (
                    (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                    "NextCheckAt" NULLS FIRST,
                    "LastCheckedAt" NULLS FIRST);
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationClaimUnleased";
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ExpiredLeaseClaim";
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Proxies" AS proxy
                SET "CheckLeaseId" = lease."LeaseId",
                    "CheckLeaseUntil" = lease."LeaseUntil"
                FROM "ProxyValidationLeases" AS lease
                WHERE proxy."Id" = lease."ProxyId";
                """);

            migrationBuilder.DropTable(
                name: "ProxyValidationLeases");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_CheckLeaseId",
                table: "Proxies",
                column: "CheckLeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_NextCheckAt_CheckLeaseUntil",
                table: "Proxies",
                columns: new[] { "NextCheckAt", "CheckLeaseUntil" });

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ValidationClaimUnleased"
                ON "Proxies" (
                    (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                    "NextCheckAt" NULLS FIRST,
                    "LastCheckedAt" NULLS FIRST)
                WHERE "CheckLeaseUntil" IS NULL;
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_ExpiredLeaseClaim"
                ON "Proxies" ("CheckLeaseUntil")
                WHERE "CheckLeaseUntil" IS NOT NULL;
                """, suppressTransaction: true);
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_ValidationQueueOrder";
                """, suppressTransaction: true);
        }
    }
}
