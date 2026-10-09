using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveVpnConnectionProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Payload",
                table: "VpnSourceImportStates");

            migrationBuilder.AddColumn<int>(
                name: "ProfileNextIndex",
                table: "VpnSourceImportStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProfileRecordCount",
                table: "VpnSourceImportStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Preserve endpoint progress while the new independent cursor revisits every original record.
            migrationBuilder.Sql("""
                UPDATE "VpnSourceImportStates"
                SET "ProfileRecordCount" = (get_byte("Payload", 4)::bigint +
                    get_byte("Payload", 5)::bigint * 256 + get_byte("Payload", 6)::bigint * 65536 +
                    get_byte("Payload", 7)::bigint * 16777216)::integer
                WHERE octet_length("Payload") > 16
                """);

            // Completed legacy snapshots no longer contain original profiles. Request a full body
            // at the next permitted refresh; preserve provider cooldowns and source health.
            migrationBuilder.Sql("""
                UPDATE "VpnSources" source
                SET "HttpETag" = NULL, "HttpLastModifiedAt" = NULL
                WHERE EXISTS (SELECT 1 FROM "VpnSourceImportStates" state
                    WHERE state."VpnSourceId" = source."Id" AND octet_length(state."Payload") = 0)
                """);

            migrationBuilder.CreateTable(
                name: "VpnConnectionProfiles",
                columns: table => new
                {
                    VpnSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    Transport = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ConnectionUri = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ClashConfiguration = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnConnectionProfiles", x => new { x.VpnSourceId, x.ProfileHash });
                    table.CheckConstraint("CK_VpnConnectionProfiles_Identity", "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 14 AND \"Transport\" IN ('tcp', 'udp') AND \"ProfileHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_VpnConnectionProfiles_Settings", "\"ConnectionUri\" IS NOT NULL OR \"ClashConfiguration\" IS NOT NULL");
                    table.CheckConstraint("CK_VpnConnectionProfiles_Timeline", "\"LastSeenAt\" >= \"FirstSeenAt\"");
                    table.ForeignKey(
                        name: "FK_VpnConnectionProfiles_VpnSources_VpnSourceId",
                        column: x => x.VpnSourceId,
                        principalTable: "VpnSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 14 AND \"ProfileRecordCount\" BETWEEN 0 AND 1000000 AND \"ProfileNextIndex\" BETWEEN 0 AND \"ProfileRecordCount\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Payload",
                table: "VpnSourceImportStates",
                sql: "octet_length(\"Payload\") <= 50331648 AND octet_length(\"SnapshotBodyHash\") = 32 AND octet_length(\"FreshBodyHash\") = 32 AND (((\"NextIndex\" < \"CandidateCount\" OR \"ProfileNextIndex\" < \"ProfileRecordCount\") AND octet_length(\"Payload\") > 16 AND octet_length(\"PayloadHash\") = 32) OR (\"NextIndex\" = \"CandidateCount\" AND \"ProfileNextIndex\" = \"ProfileRecordCount\" AND octet_length(\"Payload\") = 0 AND octet_length(\"PayloadHash\") = 0))");

            migrationBuilder.CreateIndex(
                name: "IX_VpnConnectionProfiles_Host_Port_Protocol_Transport",
                table: "VpnConnectionProfiles",
                columns: new[] { "Host", "Port", "Protocol", "Transport" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous schema cannot represent an endpoint-complete snapshot with pending profiles.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "VpnSourceImportStates"
                        WHERE "NextIndex" = "CandidateCount" AND octet_length("Payload") > 0) THEN
                        RAISE EXCEPTION 'Finish pending VPN profile imports before rolling back this migration';
                    END IF;
                END $$
                """);

            migrationBuilder.DropTable(
                name: "VpnConnectionProfiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnSourceImportStates_Payload",
                table: "VpnSourceImportStates");

            migrationBuilder.DropColumn(
                name: "ProfileNextIndex",
                table: "VpnSourceImportStates");

            migrationBuilder.DropColumn(
                name: "ProfileRecordCount",
                table: "VpnSourceImportStates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Cursor",
                table: "VpnSourceImportStates",
                sql: "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 14");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnSourceImportStates_Payload",
                table: "VpnSourceImportStates",
                sql: "octet_length(\"Payload\") <= 50331648 AND octet_length(\"SnapshotBodyHash\") = 32 AND octet_length(\"FreshBodyHash\") = 32 AND ((\"NextIndex\" < \"CandidateCount\" AND octet_length(\"Payload\") > 16 AND octet_length(\"PayloadHash\") = 32) OR (\"NextIndex\" = \"CandidateCount\" AND octet_length(\"Payload\") = 0 AND octet_length(\"PayloadHash\") = 0))");
        }
    }
}
