using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ValidationRunAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ValidationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Claimed = table.Column<int>(type: "integer", nullable: false),
                    Checked = table.Column<int>(type: "integer", nullable: false),
                    Alive = table.Column<int>(type: "integer", nullable: false),
                    Deferred = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_CheckLeaseId",
                table: "Proxies",
                column: "CheckLeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_LeaseId",
                table: "ValidationRuns",
                column: "LeaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_StartedAt",
                table: "ValidationRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_Status_FinishedAt",
                table: "ValidationRuns",
                columns: new[] { "Status", "FinishedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ValidationRuns");

            migrationBuilder.DropIndex(
                name: "IX_Proxies_CheckLeaseId",
                table: "Proxies");
        }
    }
}
