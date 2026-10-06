using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProxySourceImportSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxySourceImportStates",
                columns: table => new
                {
                    ProxySourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceProtocol = table.Column<int>(type: "integer", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    NextIndex = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    PayloadHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    StoredBytes = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "octet_length(\"Payload\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxySourceImportStates", x => x.ProxySourceId);
                    table.CheckConstraint("CK_ProxySourceImportStates_Cursor", "\"CandidateCount\" BETWEEN 1 AND 1000000 AND \"NextIndex\" BETWEEN 0 AND \"CandidateCount\" AND \"SourceProtocol\" BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_ProxySourceImportStates_Payload", "octet_length(\"Payload\") <= 24000000 AND ((\"NextIndex\" < \"CandidateCount\" AND octet_length(\"Payload\") > 8 AND octet_length(\"PayloadHash\") = 32) OR (\"NextIndex\" = \"CandidateCount\" AND octet_length(\"Payload\") = 0 AND octet_length(\"PayloadHash\") = 0))");
                    table.ForeignKey(
                        name: "FK_ProxySourceImportStates_Sources_ProxySourceId",
                        column: x => x.ProxySourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxySourceImportStates");
        }
    }
}
