using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceApiPageCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceApiCaptureStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProxySourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    VpnSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceProtocol = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    PayloadHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoredBytes = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "octet_length(\"Payload\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceApiCaptureStates", x => x.Id);
                    table.CheckConstraint("CK_SourceApiCaptureStates_Owner", "(\"ProxySourceId\" IS NOT NULL AND \"VpnSourceId\" IS NULL AND \"SourceProtocol\" BETWEEN 0 AND 3) OR (\"ProxySourceId\" IS NULL AND \"VpnSourceId\" IS NOT NULL AND \"SourceProtocol\" BETWEEN 0 AND 7)");
                    table.CheckConstraint("CK_SourceApiCaptureStates_Payload", "octet_length(\"Payload\") BETWEEN 8 AND 34554432 AND octet_length(\"PayloadHash\") = 32");
                    table.ForeignKey(
                        name: "FK_SourceApiCaptureStates_Sources_ProxySourceId",
                        column: x => x.ProxySourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceApiCaptureStates_VpnSources_VpnSourceId",
                        column: x => x.VpnSourceId,
                        principalTable: "VpnSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceApiOriginStates",
                columns: table => new
                {
                    Origin = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceApiOriginStates", x => x.Origin);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceApiCaptureStates_ProxySourceId",
                table: "SourceApiCaptureStates",
                column: "ProxySourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceApiCaptureStates_VpnSourceId",
                table: "SourceApiCaptureStates",
                column: "VpnSourceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceApiCaptureStates");

            migrationBuilder.DropTable(
                name: "SourceApiOriginStates");
        }
    }
}
