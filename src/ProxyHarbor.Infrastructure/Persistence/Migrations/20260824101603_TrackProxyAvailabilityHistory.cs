using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackProxyAvailabilityHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CurrentAliveSince",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstAliveAt",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAliveAt",
                table: "Proxies",
                type: "timestamp with time zone",
                nullable: true);

            // Старые строки уже содержат доказательство успешных проверок в счётчике.
            // FirstSeenAt — консервативная нижняя граница истории, а для текущих Alive
            // непрерывная серия начинается с последней подтверждённой проверки.
            migrationBuilder.Sql("""
                UPDATE "Proxies"
                SET "FirstAliveAt" = "FirstSeenAt",
                    "LastAliveAt" = CASE WHEN "Status" = 1 THEN "LastCheckedAt" ELSE "FirstSeenAt" END,
                    "CurrentAliveSince" = CASE WHEN "Status" = 1 THEN "LastCheckedAt" ELSE NULL END
                WHERE "SuccessfulChecks" > 0
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'CK_Proxies_AliveTimeline'
                          AND conrelid = '"Proxies"'::regclass)
                    THEN
                        ALTER TABLE "Proxies" ADD CONSTRAINT "CK_Proxies_AliveTimeline"
                            CHECK (("FirstAliveAt" IS NULL) = ("LastAliveAt" IS NULL) AND ("FirstAliveAt" IS NULL OR ("FirstAliveAt" >= "FirstSeenAt" AND "LastAliveAt" >= "FirstAliveAt")) AND ("CurrentAliveSince" IS NULL OR ("Status" = 1 AND "FirstAliveAt" IS NOT NULL AND "CurrentAliveSince" >= "FirstAliveAt" AND "LastAliveAt" >= "CurrentAliveSince"))) NOT VALID;
                    END IF;
                END
                $migration$;
                """, suppressTransaction: true);
            migrationBuilder.Sql(
                """ALTER TABLE "Proxies" VALIDATE CONSTRAINT "CK_Proxies_AliveTimeline";""",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "Proxies" DROP CONSTRAINT IF EXISTS "CK_Proxies_AliveTimeline";""",
                suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "CurrentAliveSince",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "FirstAliveAt",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "LastAliveAt",
                table: "Proxies");
        }
    }
}
