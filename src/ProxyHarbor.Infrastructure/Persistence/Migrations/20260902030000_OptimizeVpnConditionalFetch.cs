using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeVpnConditionalFetch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Sql(migrationBuilder, """
                ALTER TABLE "VpnSources"
                    ADD COLUMN IF NOT EXISTS "HttpETag" character varying(512),
                    ADD COLUMN IF NOT EXISTS "HttpLastModifiedAt" timestamp with time zone,
                    ADD COLUMN IF NOT EXISTS "LastContentFetchedAt" timestamp with time zone;
                """);
            Sql(migrationBuilder, """
                DO $migration$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conrelid = '"VpnSources"'::regclass
                          AND conname = 'CK_VpnSources_ContentTimeline') THEN
                        ALTER TABLE "VpnSources" ADD CONSTRAINT "CK_VpnSources_ContentTimeline"
                        CHECK (
                            "LastContentFetchedAt" IS NULL OR (
                                "LastFetchedAt" IS NOT NULL AND
                                "LastSucceededAt" IS NOT NULL AND
                                "LastContentFetchedAt" <= "LastFetchedAt" AND
                                "LastContentFetchedAt" <= "LastSucceededAt")) NOT VALID;
                    END IF;
                END
                $migration$;
                """);
            Sql(migrationBuilder,
                """ALTER TABLE "VpnSources" VALIDATE CONSTRAINT "CK_VpnSources_ContentTimeline";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Sql(migrationBuilder, """
                ALTER TABLE "VpnSources" DROP CONSTRAINT IF EXISTS "CK_VpnSources_ContentTimeline";
                ALTER TABLE "VpnSources"
                    DROP COLUMN IF EXISTS "HttpETag",
                    DROP COLUMN IF EXISTS "HttpLastModifiedAt",
                    DROP COLUMN IF EXISTS "LastContentFetchedAt";
                """);
        }

        private static void Sql(MigrationBuilder migrationBuilder, string command) =>
            migrationBuilder.Sql(command, suppressTransaction: true);
    }
}
