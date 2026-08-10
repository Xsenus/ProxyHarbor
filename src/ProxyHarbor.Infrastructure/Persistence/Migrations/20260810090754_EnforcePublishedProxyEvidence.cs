using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePublishedProxyEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT VALID сразу защищает новые writes, но допускает безопасное исправление
            // исторических строк без длительной эксклюзивной блокировки таблицы.
            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'CK_Proxies_StatusEvidence'
                          AND conrelid = '"Proxies"'::regclass)
                    THEN
                        ALTER TABLE "Proxies" ADD CONSTRAINT "CK_Proxies_StatusEvidence"
                            CHECK (("Status" = 0) OR ("Status" = 1 AND "LastCheckedAt" IS NOT NULL AND "LatencyMs" IS NOT NULL AND "SuccessfulChecks" > 0) OR ("Status" = 2 AND "LastCheckedAt" IS NOT NULL AND "FailedChecks" > 0)) NOT VALID;
                    END IF;
                END
                $migration$;
                """,
                suppressTransaction: true);

            // Неподтверждённые статусы не удаляются: они возвращаются в немедленную
            // очередь проверки и перестают попадать в публичную выдачу.
            migrationBuilder.Sql(
                """
                UPDATE "Proxies"
                SET "Status" = 0, "NextCheckAt" = NULL
                WHERE ("Status" = 1 AND ("LastCheckedAt" IS NULL OR "LatencyMs" IS NULL OR "SuccessfulChecks" <= 0))
                   OR ("Status" = 2 AND ("LastCheckedAt" IS NULL OR "FailedChecks" <= 0));
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """ALTER TABLE "Proxies" VALIDATE CONSTRAINT "CK_Proxies_StatusEvidence";""",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "Proxies" DROP CONSTRAINT IF EXISTS "CK_Proxies_StatusEvidence";""",
                suppressTransaction: true);
        }
    }
}
