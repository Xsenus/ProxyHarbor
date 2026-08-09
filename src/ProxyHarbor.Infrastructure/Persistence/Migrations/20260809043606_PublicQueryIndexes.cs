using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Status_LastSeenAt",
                table: "Proxies",
                columns: new[] { "Status", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt",
                table: "Proxies",
                columns: new[] { "Status", "Protocol", "LatencyMs", "LastCheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Proxies_Status_LastSeenAt",
                table: "Proxies");

            migrationBuilder.DropIndex(
                name: "IX_Proxies_Status_Protocol_LatencyMs_LastCheckedAt",
                table: "Proxies");
        }
    }
}
