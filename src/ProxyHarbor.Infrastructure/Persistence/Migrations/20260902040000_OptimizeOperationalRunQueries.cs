using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeOperationalRunQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Runs_FinishedAt",
                table: "Runs",
                column: "FinishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_StartedAt",
                table: "Runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status_FinishedAt",
                table: "Runs",
                columns: new[] { "Status", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_FinishedAt",
                table: "BackupRuns",
                column: "FinishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runs_FinishedAt",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_StartedAt",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Status_FinishedAt",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_BackupRuns_FinishedAt",
                table: "BackupRuns");
        }
    }
}
