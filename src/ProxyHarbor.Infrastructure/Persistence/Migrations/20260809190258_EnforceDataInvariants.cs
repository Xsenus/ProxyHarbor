using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceDataInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddAndValidateConstraint(migrationBuilder, "ValidationRuns", "CK_ValidationRuns_Counters",
                "\"Claimed\" >= 0 AND \"Checked\" >= 0 AND \"Alive\" >= 0 AND \"Deferred\" >= 0 AND \"Checked\"::bigint + \"Deferred\"::bigint <= \"Claimed\" AND \"Alive\" <= \"Checked\"");
            AddAndValidateConstraint(migrationBuilder, "ValidationRuns", "CK_ValidationRuns_State",
                "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            AddAndValidateConstraint(migrationBuilder, "Sources", "CK_Sources_Counters",
                "\"LastItemCount\" >= 0 AND \"ConsecutiveFailures\" >= 0");
            AddAndValidateConstraint(migrationBuilder, "Sources", "CK_Sources_FetchTimeline",
                "\"LastSucceededAt\" IS NULL OR (\"LastFetchedAt\" IS NOT NULL AND \"LastSucceededAt\" <= \"LastFetchedAt\")");
            AddAndValidateConstraint(migrationBuilder, "Sources", "CK_Sources_ProtocolPriority",
                "\"DefaultProtocol\" BETWEEN 0 AND 3 AND \"Priority\" BETWEEN -10000 AND 10000");
            AddAndValidateConstraint(migrationBuilder, "Runs", "CK_Runs_Counters",
                "\"SourcesProcessed\" >= 0 AND \"SourcesSucceeded\" >= 0 AND \"SourcesFailed\" >= 0 AND \"SourcesSkipped\" >= 0 AND \"SourcesTruncated\" >= 0 AND \"CandidatesFound\" >= 0 AND \"NewProxies\" >= 0 AND \"AliveProxies\" >= 0 AND \"SourcesSucceeded\"::bigint + \"SourcesFailed\"::bigint = \"SourcesProcessed\" AND \"SourcesTruncated\" <= \"SourcesSucceeded\" AND \"NewProxies\" <= \"CandidatesFound\"");
            AddAndValidateConstraint(migrationBuilder, "Runs", "CK_Runs_State",
                "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_CheckCounters",
                "\"SuccessfulChecks\" >= 0 AND \"FailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" >= 0 AND \"ConsecutiveFailedChecks\" <= \"FailedChecks\" AND \"SuccessfulChecks\"::bigint + \"FailedChecks\"::bigint <= 2147483647");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_DeferredAttempt",
                "NOT \"LastValidationDeferred\" OR \"LastValidationAttemptAt\" IS NOT NULL");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_Identity",
                "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 3 AND \"Status\" BETWEEN 0 AND 2");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_Latency",
                "\"LatencyMs\" IS NULL OR \"LatencyMs\" >= 0");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_Lease",
                "(\"CheckLeaseUntil\" IS NULL) = (\"CheckLeaseId\" IS NULL)");
            AddAndValidateConstraint(migrationBuilder, "Proxies", "CK_Proxies_Timeline",
                "\"LastSeenAt\" >= \"FirstSeenAt\"");
            AddAndValidateConstraint(migrationBuilder, "BackupRuns", "CK_BackupRuns_Result",
                "\"SizeBytes\" >= 0 AND (NOT \"SentToTelegram\" OR \"TelegramConfigured\") AND (\"Status\" <> 'completed' OR NOT \"TelegramConfigured\" OR \"SentToTelegram\")");
            AddAndValidateConstraint(migrationBuilder, "BackupRuns", "CK_BackupRuns_State",
                "\"Status\" IN ('running', 'completed', 'failed') AND ((\"Status\" = 'running') = (\"FinishedAt\" IS NULL)) AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ValidationRuns_Counters",
                table: "ValidationRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ValidationRuns_State",
                table: "ValidationRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sources_Counters",
                table: "Sources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sources_FetchTimeline",
                table: "Sources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sources_ProtocolPriority",
                table: "Sources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Runs_Counters",
                table: "Runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Runs_State",
                table: "Runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_CheckCounters",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_DeferredAttempt",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Identity",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Latency",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Lease",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Timeline",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_Result",
                table: "BackupRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupRuns_State",
                table: "BackupRuns");
        }

        private static void AddAndValidateConstraint(
            MigrationBuilder migrationBuilder,
            string table,
            string name,
            string expression)
        {
            // NOT VALID защищает новые строки сразу, но не требует блокирующего table scan.
            // Идемпотентный DO позволяет безопасно повторить миграцию после отказа VALIDATE.
            migrationBuilder.Sql($$"""
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = '{{name}}' AND conrelid = '"{{table}}"'::regclass)
                    THEN
                        ALTER TABLE "{{table}}" ADD CONSTRAINT "{{name}}" CHECK ({{expression}}) NOT VALID;
                    END IF;
                END
                $migration$;
                """, suppressTransaction: true);
            // VALIDATE использует более мягкую блокировку и не останавливает обычные writes.
            migrationBuilder.Sql(
                $"ALTER TABLE \"{table}\" VALIDATE CONSTRAINT \"{name}\";",
                suppressTransaction: true);
        }
    }
}
