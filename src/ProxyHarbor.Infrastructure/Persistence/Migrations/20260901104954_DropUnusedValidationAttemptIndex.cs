using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedValidationAttemptIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Индекс обслуживается при каждой validation-записи, но production
            // pg_stat_user_indexes не зафиксировал ни одного чтения. CONCURRENTLY
            // не блокирует непрерывный validation pipeline во время deployment.
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Proxies_LastValidationAttemptAt\"",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_Proxies_LastValidationAttemptAt\" " +
                "ON \"Proxies\" (\"LastValidationAttemptAt\")",
                suppressTransaction: true);
        }
    }
}
