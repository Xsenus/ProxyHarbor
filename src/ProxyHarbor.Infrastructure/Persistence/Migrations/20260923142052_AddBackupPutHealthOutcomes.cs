using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupPutHealthOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_Operation",
                table: "BackupDestinationHealthOutcomes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_Operation",
                table: "BackupDestinationHealthOutcomes",
                sql: "\"Operation\" IN ('put', 'verify')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_Operation",
                table: "BackupDestinationHealthOutcomes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_Operation",
                table: "BackupDestinationHealthOutcomes",
                sql: "\"Operation\" = 'verify'");
        }
    }
}
