using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaidProxySourceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxySourceCredentials",
                columns: table => new
                {
                    ProxySourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxySourceCredentials", x => x.ProxySourceId);
                    table.CheckConstraint("CK_ProxySourceCredentials_Status", "\"Status\" IN ('not_configured', 'active', 'expired', 'invalid', 'rate_limited', 'error')");
                    table.ForeignKey(
                        name: "FK_ProxySourceCredentials_Sources_ProxySourceId",
                        column: x => x.ProxySourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Обычно маркер присутствует только у нескольких тысяч адресов платного
            // provider. Частичный индекс сохраняет мгновенный priority-claim без
            // обслуживания ещё одного полного индекса на миллионной таблице.
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Proxies_PaidValidationPriority"
                ON "Proxies" (
                    (CASE "Status" WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END),
                    "LastCheckedAt" NULLS FIRST)
                WHERE "NextCheckAt" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_Proxies_PaidValidationPriority\"",
                suppressTransaction: true);
            migrationBuilder.DropTable(
                name: "ProxySourceCredentials");
        }
    }
}
