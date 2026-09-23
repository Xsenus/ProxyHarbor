using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupVerifyProbeOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProbeOutcome",
                table: "BackupDestinationHealthOutcomes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_ProbeOutcome",
                table: "BackupDestinationHealthOutcomes",
                sql: "\"ProbeOutcome\" IS NULL OR \"ProbeOutcome\" IN ('matching', 'missing', 'mismatching', 'inconclusive', 'invalid')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupDestinationHealthOutcomes_ProbeOutcome",
                table: "BackupDestinationHealthOutcomes");

            migrationBuilder.DropColumn(
                name: "ProbeOutcome",
                table: "BackupDestinationHealthOutcomes");
        }
    }
}
