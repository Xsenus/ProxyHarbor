using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizePublicQueryOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Concurrent index build нельзя выполнять внутри transaction. Каждая команда
            // suppressTransaction сохраняет доступность writers во время rolling deploy.
            // Предварительный DROP делает повтор безопасным после оборванного CREATE,
            // который PostgreSQL мог оставить как invalid index с занятым именем.
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_Protocol_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_Protocol_PublicOrder");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_PublicOrder");

            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Alive_LastCheckedAt" ON "Proxies" ("LastCheckedAt") WHERE "Status" = 1;""");
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Alive_Protocol_LastCheckedAt" ON "Proxies" ("Protocol", "LastCheckedAt") WHERE "Status" = 1;""");
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Alive_Protocol_PublicOrder" ON "Proxies" ("Protocol", "LatencyMs", "SuccessfulChecks" DESC, "Id", "LastCheckedAt") WHERE "Status" = 1;""");
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Alive_PublicOrder" ON "Proxies" ("LatencyMs", "SuccessfulChecks" DESC, "Id", "LastCheckedAt") WHERE "Status" = 1;""");

            // Старые индексы удаляются только после полной публикации замены.
            DropConcurrently(migrationBuilder, "IX_Proxies_Status_LatencyMs_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropConcurrently(migrationBuilder, "IX_Proxies_Status_LatencyMs_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt");
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Status_LatencyMs_LastCheckedAt" ON "Proxies" ("Status", "LatencyMs", "LastCheckedAt");""");
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY "IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt" ON "Proxies" ("Status", "Protocol", "LatencyMs", "LastCheckedAt");""");

            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_Protocol_LastCheckedAt");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_Protocol_PublicOrder");
            DropConcurrently(migrationBuilder, "IX_Proxies_Alive_PublicOrder");
        }

        private static void DropConcurrently(MigrationBuilder migrationBuilder, string indexName) =>
            Sql(migrationBuilder, $"DROP INDEX CONCURRENTLY IF EXISTS \"{indexName}\";");

        private static void Sql(MigrationBuilder migrationBuilder, string command) =>
            migrationBuilder.Sql(command, suppressTransaction: true);
    }
}
