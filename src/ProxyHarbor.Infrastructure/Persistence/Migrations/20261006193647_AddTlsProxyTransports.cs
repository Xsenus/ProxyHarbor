using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTlsProxyTransports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Sources_ProtocolPriority",
                table: "Sources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProxySourceImportStates_Cursor",
                table: "ProxySourceImportStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Identity",
                table: "Proxies");

            migrationBuilder.AddColumn<bool>(
                name: "SupportsTlsProxyTransport",
                table: "CheckerNodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sources_ProtocolPriority",
                table: "Sources",
                sql: "\"DefaultProtocol\" BETWEEN 0 AND 5 AND \"Priority\" BETWEEN -10000 AND 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates",
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 5) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 8)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProxySourceImportStates_Cursor",
                table: "ProxySourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 5");

            migrationBuilder.CreateIndex(
                name: "IX_Proxies_Tls_NextCheckAt",
                table: "Proxies",
                column: "NextCheckAt",
                filter: "\"Protocol\" >= 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Proxies_Identity",
                table: "Proxies",
                sql: "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 5 AND \"Status\" BETWEEN 0 AND 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Sources_ProtocolPriority",
                table: "Sources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProxySourceImportStates_Cursor",
                table: "ProxySourceImportStates");

            migrationBuilder.DropIndex(
                name: "IX_Proxies_Tls_NextCheckAt",
                table: "Proxies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Proxies_Identity",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "SupportsTlsProxyTransport",
                table: "CheckerNodes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sources_ProtocolPriority",
                table: "Sources",
                sql: "\"DefaultProtocol\" BETWEEN 0 AND 3 AND \"Priority\" BETWEEN -10000 AND 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates",
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 3) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 8)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProxySourceImportStates_Cursor",
                table: "ProxySourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Proxies_Identity",
                table: "Proxies",
                sql: "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 3 AND \"Status\" BETWEEN 0 AND 2");
        }
    }
}
