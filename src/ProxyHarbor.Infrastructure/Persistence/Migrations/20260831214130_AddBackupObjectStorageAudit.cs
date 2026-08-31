using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupObjectStorageAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_Result",
                table: "BackupRuns");

            migrationBuilder.AddColumn<bool>(
                name: "ObjectStorageConfigured",
                table: "BackupRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ObjectStorageKey",
                table: "BackupRuns",
                type: "character varying(768)",
                maxLength: 768,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SentToObjectStorage",
                table: "BackupRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupRuns_Result",
                table: "BackupRuns",
                sql: "\"SizeBytes\" >= 0 AND (NOT \"SentToTelegram\" OR \"TelegramConfigured\") AND (NOT \"SentToObjectStorage\" OR \"ObjectStorageConfigured\") AND (\"ObjectStorageKey\" IS NULL OR \"SentToObjectStorage\") AND (\"Status\" <> 'completed' OR NOT \"TelegramConfigured\" OR \"SentToTelegram\") AND (\"Status\" <> 'completed' OR NOT \"ObjectStorageConfigured\" OR \"SentToObjectStorage\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_Result",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "ObjectStorageConfigured",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "ObjectStorageKey",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "SentToObjectStorage",
                table: "BackupRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupRuns_Result",
                table: "BackupRuns",
                sql: "\"SizeBytes\" >= 0 AND (NOT \"SentToTelegram\" OR \"TelegramConfigured\") AND (\"Status\" <> 'completed' OR NOT \"TelegramConfigured\" OR \"SentToTelegram\")");
        }
    }
}
