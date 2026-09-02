using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeAccessRegistryLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProxyAccessBuckets_IpAddress_LastSeenAt_Id",
                table: "ProxyAccessBuckets",
                columns: new[] { "IpAddress", "LastSeenAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"UserId\" IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "UserId", "Endpoint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProxyAccessBuckets_IpAddress_LastSeenAt_Id",
                table: "ProxyAccessBuckets");
        }
    }
}
