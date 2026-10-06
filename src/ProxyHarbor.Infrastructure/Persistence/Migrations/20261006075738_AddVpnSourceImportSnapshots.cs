using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVpnSourceImportSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VpnSourceImportStates",
                columns: table => new
                {
                    VpnSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceProtocol = table.Column<int>(type: "integer", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    NextIndex = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    PayloadHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    SnapshotBodyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    FreshBodyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    PreferFresh = table.Column<bool>(type: "boolean", nullable: false),
                    StoredBytes = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "octet_length(\"Payload\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnSourceImportStates", x => x.VpnSourceId);
                    table.CheckConstraint("CK_VpnSourceImportStates_Cursor", "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_VpnSourceImportStates_Payload", "octet_length(\"Payload\") <= 50331648 AND octet_length(\"SnapshotBodyHash\") = 32 AND octet_length(\"FreshBodyHash\") = 32 AND ((\"NextIndex\" < \"CandidateCount\" AND octet_length(\"Payload\") > 16 AND octet_length(\"PayloadHash\") = 32) OR (\"NextIndex\" = \"CandidateCount\" AND octet_length(\"Payload\") = 0 AND octet_length(\"PayloadHash\") = 0))");
                    table.ForeignKey(
                        name: "FK_VpnSourceImportStates_VpnSources_VpnSourceId",
                        column: x => x.VpnSourceId,
                        principalTable: "VpnSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VpnSourceImportStates");
        }
    }
}
