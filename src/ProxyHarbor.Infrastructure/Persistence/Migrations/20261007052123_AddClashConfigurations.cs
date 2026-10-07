using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClashConfigurations : Migration
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

            migrationBuilder.AddColumn<string>(
                name: "ClashConfiguration",
                table: "VpnEndpoints",
                type: "character varying(16384)",
                maxLength: 16384,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClashConfigurationObservedAt",
                table: "VpnEndpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSources_ProtocolPriority",
                table: "VpnSources",
                sql: "\"DefaultProtocol\" BETWEEN 0 AND 14 AND \"Priority\" BETWEEN -10000 AND 10000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 14");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnEndpoints_Identity",
                table: "VpnEndpoints",
                sql: "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 14 AND \"Status\" BETWEEN 0 AND 3 AND \"Transport\" IN ('tcp', 'udp')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceApiCaptureStates_Owner",
                table: "SourceApiCaptureStates",
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 5) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 14)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "VpnEndpoints"
                        WHERE "ClashConfiguration" IS NOT NULL OR "ClashConfigurationObservedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove saved Clash configurations or observation times.'
                            USING ERRCODE = '23514';
                    END IF;
                END $$;
                """);

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

            migrationBuilder.DropColumn(
                name: "ClashConfiguration",
                table: "VpnEndpoints");

            migrationBuilder.DropColumn(
                name: "ClashConfigurationObservedAt",
                table: "VpnEndpoints");

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
                sql: "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 5) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 8)");
        }
    }
}
