using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceContentRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastContentFetchedAt",
                table: "Sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """ALTER TABLE "Sources" ADD CONSTRAINT "CK_Sources_ContentTimeline" CHECK ("LastContentFetchedAt" IS NULL OR ("LastFetchedAt" IS NOT NULL AND "LastSucceededAt" IS NOT NULL AND "LastContentFetchedAt" <= "LastFetchedAt" AND "LastContentFetchedAt" <= "LastSucceededAt")) NOT VALID;""",
                suppressTransaction: true);
            migrationBuilder.Sql(
                """ALTER TABLE "Sources" VALIDATE CONSTRAINT "CK_Sources_ContentTimeline";""",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "Sources" DROP CONSTRAINT "CK_Sources_ContentTimeline";""",
                suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "LastContentFetchedAt",
                table: "Sources");
        }
    }
}
