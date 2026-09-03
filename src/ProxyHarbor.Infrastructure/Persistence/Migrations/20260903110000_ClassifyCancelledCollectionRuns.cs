using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassifyCancelledCollectionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Runs_State",
                table: "Runs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Runs_State",
                table: "Runs",
                sql: "\"Status\" IN ('running', 'completed', 'failed', 'cancelled') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Runs_State",
                table: "Runs");

            // Старое schema-state не знает cancelled; безопасный rollback сохраняет
            // terminal audit как failed вместо отказа при добавлении constraint.
            migrationBuilder.Sql("UPDATE \"Runs\" SET \"Status\" = 'failed' WHERE \"Status\" = 'cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Runs_State",
                table: "Runs",
                sql: "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
        }
    }
}
