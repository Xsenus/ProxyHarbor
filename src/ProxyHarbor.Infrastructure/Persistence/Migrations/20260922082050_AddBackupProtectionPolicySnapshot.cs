using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupProtectionPolicySnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BackupPoolId",
                table: "BackupRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                table: "BackupRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DesiredVerifiedCopies",
                table: "BackupRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProtectionPolicyVersion",
                table: "BackupRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequiredVerifiedCopies",
                table: "BackupRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupRuns_Content",
                table: "BackupRuns",
                sql: "\"ContentSha256\" IS NULL OR \"ContentSha256\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupRuns_ProtectionPolicy",
                table: "BackupRuns",
                sql: "(\"BackupPoolId\" IS NULL AND \"ProtectionPolicyVersion\" IS NULL AND \"RequiredVerifiedCopies\" IS NULL AND \"DesiredVerifiedCopies\" IS NULL) OR (\"BackupPoolId\" IS NOT NULL AND \"ProtectionPolicyVersion\" IS NOT NULL AND \"RequiredVerifiedCopies\" IS NOT NULL AND \"DesiredVerifiedCopies\" IS NOT NULL AND \"ProtectionPolicyVersion\" >= 1 AND \"RequiredVerifiedCopies\" BETWEEN 1 AND 16 AND \"DesiredVerifiedCopies\" BETWEEN \"RequiredVerifiedCopies\" AND 16)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_Content",
                table: "BackupRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_ProtectionPolicy",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "BackupPoolId",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "DesiredVerifiedCopies",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "ProtectionPolicyVersion",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "RequiredVerifiedCopies",
                table: "BackupRuns");
        }
    }
}
