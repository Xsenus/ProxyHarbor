using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeProxyLastSeenWriteAmplification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала сохраняем быстрый status lookup, затем убираем LastSeenAt из
            // индексируемых полей. CONCURRENTLY не блокирует живые сборы и проверки.
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_Status" ON "Proxies" ("Status");""");
            Sql(migrationBuilder,
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_Status_LastSeenAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Sql(migrationBuilder,
                """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_Status_LastSeenAt" ON "Proxies" ("Status", "LastSeenAt");""");
            Sql(migrationBuilder,
                """DROP INDEX CONCURRENTLY IF EXISTS "IX_Proxies_Status";""");
        }

        private static void Sql(MigrationBuilder migrationBuilder, string command) =>
            migrationBuilder.Sql(command, suppressTransaction: true);
    }
}
