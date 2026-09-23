using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupCatalogPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CatalogAttempt",
                table: "BackupCopies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CatalogKeyReference",
                table: "BackupCopies",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogLastErrorCode",
                table: "BackupCopies",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CatalogLeaseId",
                table: "BackupCopies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CatalogLeaseUntil",
                table: "BackupCopies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CatalogNotBefore",
                table: "BackupCopies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogObjectKey",
                table: "BackupCopies",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CatalogPublishedAt",
                table: "BackupCopies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogState",
                table: "BackupCopies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupCopies_State_CatalogState_CatalogNotBefore_CatalogLea~",
                table: "BackupCopies",
                columns: new[] { "State", "CatalogState", "CatalogNotBefore", "CatalogLeaseUntil" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupCopies_CatalogAttempt",
                table: "BackupCopies",
                sql: "\"CatalogAttempt\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupCopies_CatalogLease",
                table: "BackupCopies",
                sql: "(\"CatalogLeaseId\" IS NULL) = (\"CatalogLeaseUntil\" IS NULL) AND (\"CatalogState\" = 'processing' OR \"CatalogLeaseId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupCopies_CatalogPublished",
                table: "BackupCopies",
                sql: "\"CatalogState\" <> 'published' OR (\"CatalogPublishedAt\" IS NOT NULL AND \"CatalogObjectKey\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupCopies_CatalogState",
                table: "BackupCopies",
                sql: "\"CatalogState\" IS NULL OR \"CatalogState\" IN ('pending', 'processing', 'published', 'manual_review')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupCopies_State_CatalogState_CatalogNotBefore_CatalogLea~",
                table: "BackupCopies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupCopies_CatalogAttempt",
                table: "BackupCopies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupCopies_CatalogLease",
                table: "BackupCopies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupCopies_CatalogPublished",
                table: "BackupCopies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupCopies_CatalogState",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogAttempt",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogKeyReference",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogLastErrorCode",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogLeaseId",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogLeaseUntil",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogNotBefore",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogObjectKey",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogPublishedAt",
                table: "BackupCopies");

            migrationBuilder.DropColumn(
                name: "CatalogState",
                table: "BackupCopies");
        }
    }
}
