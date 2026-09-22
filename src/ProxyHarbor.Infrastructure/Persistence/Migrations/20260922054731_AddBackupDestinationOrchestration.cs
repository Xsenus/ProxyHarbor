using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupDestinationOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDestinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    FailureDomain = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProtectedSecrets = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDestinations", x => x.Id);
                    table.CheckConstraint("CK_BackupDestinations_Kind", "\"Kind\" IN ('s3', 'telegram')");
                    table.CheckConstraint("CK_BackupDestinations_Priority", "\"Priority\" >= 0");
                    table.CheckConstraint("CK_BackupDestinations_Timeline", "\"UpdatedAt\" >= \"CreatedAt\"");
                });

            migrationBuilder.CreateTable(
                name: "BackupPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequiredVerifiedCopies = table.Column<int>(type: "integer", nullable: false),
                    DesiredVerifiedCopies = table.Column<int>(type: "integer", nullable: false),
                    MaxAttemptsPerCycle = table.Column<int>(type: "integer", nullable: false),
                    OverallDeadlineSeconds = table.Column<int>(type: "integer", nullable: false),
                    FailbackHealthyForSeconds = table.Column<int>(type: "integer", nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPools", x => x.Id);
                    table.CheckConstraint("CK_BackupPools_Policy", "\"RequiredVerifiedCopies\" BETWEEN 1 AND 16 AND \"DesiredVerifiedCopies\" BETWEEN \"RequiredVerifiedCopies\" AND 16 AND \"MaxAttemptsPerCycle\" BETWEEN 1 AND 20 AND \"OverallDeadlineSeconds\" BETWEEN 30 AND 86400 AND \"FailbackHealthyForSeconds\" BETWEEN 0 AND 604800 AND \"PolicyVersion\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "BackupCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NativeLocator = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    NativeVersion = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    NativeChecksum = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UnknownSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupCopies", x => x.Id);
                    table.CheckConstraint("CK_BackupCopies_Content", "\"SizeBytes\" >= 0 AND \"ContentSha256\" ~ '^[0-9a-f]{64}$' AND \"AttemptCount\" >= 0 AND \"PolicyVersion\" >= 1");
                    table.CheckConstraint("CK_BackupCopies_State", "\"State\" IN ('planned', 'uploading', 'verifying', 'verified', 'retryable_failed', 'permanent_failed', 'unknown', 'reconciling', 'manual_review', 'missing', 'quarantined')");
                    table.CheckConstraint("CK_BackupCopies_Unknown", "(\"State\" <> 'unknown' OR \"UnknownSince\" IS NOT NULL) AND (\"UnknownSince\" IS NULL OR \"State\" IN ('unknown', 'reconciling', 'manual_review'))");
                    table.CheckConstraint("CK_BackupCopies_Verified", "\"State\" <> 'verified' OR (\"VerifiedAt\" IS NOT NULL AND \"NativeLocator\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_BackupCopies_BackupDestinations_BackupDestinationId",
                        column: x => x.BackupDestinationId,
                        principalTable: "BackupDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupCopies_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupPoolDestinations",
                columns: table => new
                {
                    BackupPoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupDestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    AllowedOperations = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Draining = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPoolDestinations", x => new { x.BackupPoolId, x.BackupDestinationId });
                    table.CheckConstraint("CK_BackupPoolDestinations_Draining", "NOT \"Draining\" OR NOT \"Enabled\"");
                    table.CheckConstraint("CK_BackupPoolDestinations_Operations", "\"AllowedOperations\" IN ('put', 'put,verify', 'put,verify,read', 'verify', 'verify,read', 'read')");
                    table.CheckConstraint("CK_BackupPoolDestinations_Priority", "\"Priority\" >= 0");
                    table.CheckConstraint("CK_BackupPoolDestinations_Role", "\"Role\" IN ('primary', 'fallback', 'secondary')");
                    table.ForeignKey(
                        name: "FK_BackupPoolDestinations_BackupDestinations_BackupDestination~",
                        column: x => x.BackupDestinationId,
                        principalTable: "BackupDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupPoolDestinations_BackupPools_BackupPoolId",
                        column: x => x.BackupPoolId,
                        principalTable: "BackupPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupDeliveryJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupCopyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDeliveryJobs", x => x.Id);
                    table.CheckConstraint("CK_BackupDeliveryJobs_Attempt", "\"Attempt\" >= 0");
                    table.CheckConstraint("CK_BackupDeliveryJobs_Lease", "(\"LeaseId\" IS NULL) = (\"LeaseUntil\" IS NULL) AND (\"State\" = 'processing' OR \"LeaseId\" IS NULL)");
                    table.CheckConstraint("CK_BackupDeliveryJobs_State", "\"State\" IN ('pending', 'processing', 'completed', 'failed', 'reconciling', 'manual_review')");
                    table.CheckConstraint("CK_BackupDeliveryJobs_Timeline", "\"UpdatedAt\" >= \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_BackupDeliveryJobs_BackupCopies_BackupCopyId",
                        column: x => x.BackupCopyId,
                        principalTable: "BackupCopies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupRestoreVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupCopyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApplicationRevision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRestoreVerifications", x => x.Id);
                    table.CheckConstraint("CK_BackupRestoreVerifications_Environment", "\"Environment\" IN ('local', 'ci', 'isolated', 'staging', 'production')");
                    table.CheckConstraint("CK_BackupRestoreVerifications_Result", "\"Result\" IN ('running', 'passed', 'failed') AND ((\"Result\" = 'running') = (\"FinishedAt\" IS NULL))");
                    table.CheckConstraint("CK_BackupRestoreVerifications_Timeline", "\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
                    table.ForeignKey(
                        name: "FK_BackupRestoreVerifications_BackupCopies_BackupCopyId",
                        column: x => x.BackupCopyId,
                        principalTable: "BackupCopies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupRestoreVerifications_BackupRuns_BackupRunId",
                        column: x => x.BackupRunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupCopies_BackupDestinationId_VerifiedAt",
                table: "BackupCopies",
                columns: new[] { "BackupDestinationId", "VerifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupCopies_BackupRunId_BackupDestinationId",
                table: "BackupCopies",
                columns: new[] { "BackupRunId", "BackupDestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupCopies_State_LastAttemptAt",
                table: "BackupCopies",
                columns: new[] { "State", "LastAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDeliveryJobs_BackupCopyId",
                table: "BackupDeliveryJobs",
                column: "BackupCopyId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupDeliveryJobs_IdempotencyKey",
                table: "BackupDeliveryJobs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupDeliveryJobs_State_NotBefore_LeaseUntil",
                table: "BackupDeliveryJobs",
                columns: new[] { "State", "NotBefore", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinations_Enabled_Priority",
                table: "BackupDestinations",
                columns: new[] { "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinations_Name",
                table: "BackupDestinations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoolDestinations_BackupDestinationId",
                table: "BackupPoolDestinations",
                column: "BackupDestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoolDestinations_BackupPoolId_Enabled_Priority",
                table: "BackupPoolDestinations",
                columns: new[] { "BackupPoolId", "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPools_Name",
                table: "BackupPools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRestoreVerifications_BackupCopyId",
                table: "BackupRestoreVerifications",
                column: "BackupCopyId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRestoreVerifications_BackupRunId_StartedAt",
                table: "BackupRestoreVerifications",
                columns: new[] { "BackupRunId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRestoreVerifications_Result_FinishedAt",
                table: "BackupRestoreVerifications",
                columns: new[] { "Result", "FinishedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDeliveryJobs");

            migrationBuilder.DropTable(
                name: "BackupPoolDestinations");

            migrationBuilder.DropTable(
                name: "BackupRestoreVerifications");

            migrationBuilder.DropTable(
                name: "BackupPools");

            migrationBuilder.DropTable(
                name: "BackupCopies");

            migrationBuilder.DropTable(
                name: "BackupDestinations");
        }
    }
}
