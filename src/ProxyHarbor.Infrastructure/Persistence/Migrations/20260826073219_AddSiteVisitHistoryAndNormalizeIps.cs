using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteVisitHistoryAndNormalizeIps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Старые версии сохраняли IPv4 как ::ffff:x.x.x.x. Сначала складываем
            // возможные конфликтующие bucket в уже существующую каноническую строку.
            migrationBuilder.Sql("""
                UPDATE "ProxyAccessBuckets" AS target
                SET "Requests" = target."Requests" + mapped."Requests",
                    "BlockedRequests" = target."BlockedRequests" + mapped."BlockedRequests",
                    "ProxyItems" = target."ProxyItems" + mapped."ProxyItems",
                    "BytesSent" = target."BytesSent" + mapped."BytesSent",
                    "LastSeenAt" = GREATEST(target."LastSeenAt", mapped."LastSeenAt")
                FROM (
                    SELECT "BucketStartedAt", substring("IpAddress" from 8) AS "CanonicalIp",
                           "UserId", "Endpoint", SUM("Requests")::int AS "Requests",
                           SUM("BlockedRequests")::int AS "BlockedRequests",
                           SUM("ProxyItems")::bigint AS "ProxyItems",
                           SUM("BytesSent")::bigint AS "BytesSent", MAX("LastSeenAt") AS "LastSeenAt"
                    FROM "ProxyAccessBuckets"
                    WHERE "IpAddress" LIKE '::ffff:%'
                    GROUP BY "BucketStartedAt", substring("IpAddress" from 8), "UserId", "Endpoint"
                ) AS mapped
                WHERE target."BucketStartedAt" = mapped."BucketStartedAt"
                  AND target."IpAddress" = mapped."CanonicalIp"
                  AND target."UserId" IS NOT DISTINCT FROM mapped."UserId"
                  AND target."Endpoint" = mapped."Endpoint";

                DELETE FROM "ProxyAccessBuckets" AS mapped
                WHERE mapped."IpAddress" LIKE '::ffff:%'
                  AND EXISTS (
                      SELECT 1 FROM "ProxyAccessBuckets" AS canonical
                      WHERE canonical."BucketStartedAt" = mapped."BucketStartedAt"
                        AND canonical."IpAddress" = substring(mapped."IpAddress" from 8)
                        AND canonical."UserId" IS NOT DISTINCT FROM mapped."UserId"
                        AND canonical."Endpoint" = mapped."Endpoint");

                UPDATE "ProxyAccessBuckets"
                SET "IpAddress" = substring("IpAddress" from 8)
                WHERE "IpAddress" LIKE '::ffff:%';

                -- Docker/private hops не являются посетителями и искажали статистику,
                -- когда known network reverse proxy был настроен неверно.
                DELETE FROM "ProxyAccessBuckets"
                WHERE "IpAddress" ~ '^(10\\.|192\\.168\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.)';

                UPDATE "AccessBlockRules"
                SET "Value" = substring("Value" from 8)
                WHERE "Kind" = 'ip' AND "Value" LIKE '::ffff:%';
                """);

            migrationBuilder.CreateTable(
                name: "SiteVisitLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Page = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VisitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVisitLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteVisitLogs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisitLogs_IpAddress_VisitedAt",
                table: "SiteVisitLogs",
                columns: new[] { "IpAddress", "VisitedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisitLogs_UserId_VisitedAt",
                table: "SiteVisitLogs",
                columns: new[] { "UserId", "VisitedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisitLogs_VisitedAt",
                table: "SiteVisitLogs",
                column: "VisitedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteVisitLogs");
        }
    }
}
