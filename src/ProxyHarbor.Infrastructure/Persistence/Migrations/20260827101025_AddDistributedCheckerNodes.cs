using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributedCheckerNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckerNodeId",
                table: "ValidationRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CheckerNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Host = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SshPort = table.Column<int>(type: "integer", nullable: false),
                    SshUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    HostKeyFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Concurrency = table.Column<int>(type: "integer", nullable: false),
                    BatchSize = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLeaseAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CurrentLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RemoteAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeploymentStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompletedChecks = table.Column<long>(type: "bigint", nullable: false),
                    AliveChecks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckerNodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_CheckerNodeId_StartedAt",
                table: "ValidationRuns",
                columns: new[] { "CheckerNodeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckerNodes_Host",
                table: "CheckerNodes",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_CheckerNodes_LastHeartbeatAt",
                table: "CheckerNodes",
                column: "LastHeartbeatAt");

            migrationBuilder.CreateIndex(
                name: "IX_CheckerNodes_Name",
                table: "CheckerNodes",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ValidationRuns_CheckerNodes_CheckerNodeId",
                table: "ValidationRuns",
                column: "CheckerNodeId",
                principalTable: "CheckerNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ValidationRuns_CheckerNodes_CheckerNodeId",
                table: "ValidationRuns");

            migrationBuilder.DropTable(
                name: "CheckerNodes");

            migrationBuilder.DropIndex(
                name: "IX_ValidationRuns_CheckerNodeId_StartedAt",
                table: "ValidationRuns");

            migrationBuilder.DropColumn(
                name: "CheckerNodeId",
                table: "ValidationRuns");
        }
    }
}
