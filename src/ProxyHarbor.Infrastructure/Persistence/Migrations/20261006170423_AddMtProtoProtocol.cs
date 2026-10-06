using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMtProtoProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSources_ProtocolPriority",
                table: "VpnSources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnEndpoints_Identity",
                table: "VpnEndpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSources_ProtocolPriority",
                table: "VpnSources",
                sql: "\"DefaultProtocol\" BETWEEN 0 AND 8 AND \"Priority\" BETWEEN -10000 AND 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 8");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnEndpoints_Identity",
                table: "VpnEndpoints",
                sql: "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 8 AND \"Status\" BETWEEN 0 AND 3 AND \"Transport\" IN ('tcp', 'udp')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates",
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 3) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 8)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSources_ProtocolPriority",
                table: "VpnSources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnEndpoints_Identity",
                table: "VpnEndpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSources_ProtocolPriority",
                table: "VpnSources",
                sql: "\"DefaultProtocol\" BETWEEN 0 AND 7 AND \"Priority\" BETWEEN -10000 AND 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 7");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnEndpoints_Identity",
                table: "VpnEndpoints",
                sql: "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 7 AND \"Status\" BETWEEN 0 AND 3 AND \"Transport\" IN ('tcp', 'udp')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates",
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 3) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 7)");
        }
    }
}
