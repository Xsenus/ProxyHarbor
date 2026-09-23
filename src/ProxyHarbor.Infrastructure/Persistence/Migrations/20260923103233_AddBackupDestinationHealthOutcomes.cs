using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupDestinationHealthOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDestinationHealthOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDestinationHealthOutcomes", x => x.Id);
                    table.CheckConstraint("CK_BackupDestinationHealthOutcomes_Operation", "\"Operation\" = 'verify'");
                    table.CheckConstraint("CK_BackupDestinationHealthOutcomes_Result", "\"Succeeded\" = (\"ErrorCode\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupDestinationHealthOutcomes_BackupDestinations_BackupDe~",
                        column: x => x.BackupDestinationId,
                        principalTable: "BackupDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinationHealthOutcomes_BackupDestinationId_Operati~",
                table: "BackupDestinationHealthOutcomes",
                columns: new[] { "BackupDestinationId", "Operation", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinationHealthOutcomes_ObservedAt",
                table: "BackupDestinationHealthOutcomes",
                column: "ObservedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDestinationHealthOutcomes");
        }
    }
}
