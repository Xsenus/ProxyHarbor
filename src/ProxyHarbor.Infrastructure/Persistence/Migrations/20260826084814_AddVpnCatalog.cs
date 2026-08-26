using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVpnCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VpnSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DefaultProtocol = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    License = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSucceededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastItemCount = table.Column<int>(type: "integer", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnSources", x => x.Id);
                    table.CheckConstraint("CK_VpnSources_Counters", "\"LastItemCount\" >= 0 AND \"ConsecutiveFailures\" >= 0");
                    table.CheckConstraint("CK_VpnSources_ProtocolPriority", "\"DefaultProtocol\" BETWEEN 0 AND 7 AND \"Priority\" BETWEEN -10000 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "VpnEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    Transport = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuccessfulChecks = table.Column<int>(type: "integer", nullable: false),
                    FailedChecks = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FirstSourceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnEndpoints", x => x.Id);
                    table.CheckConstraint("CK_VpnEndpoints_Counters", "\"SuccessfulChecks\" >= 0 AND \"FailedChecks\" >= 0");
                    table.CheckConstraint("CK_VpnEndpoints_Identity", "\"Port\" BETWEEN 1 AND 65535 AND \"Protocol\" BETWEEN 0 AND 7 AND \"Status\" BETWEEN 0 AND 3 AND \"Transport\" IN ('tcp', 'udp')");
                    table.CheckConstraint("CK_VpnEndpoints_Timeline", "\"LastSeenAt\" >= \"FirstSeenAt\"");
                    table.ForeignKey(
                        name: "FK_VpnEndpoints_VpnSources_FirstSourceId",
                        column: x => x.FirstSourceId,
                        principalTable: "VpnSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VpnEndpointSources",
                columns: table => new
                {
                    VpnEndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    VpnSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnEndpointSources", x => new { x.VpnEndpointId, x.VpnSourceId });
                    table.ForeignKey(
                        name: "FK_VpnEndpointSources_VpnEndpoints_VpnEndpointId",
                        column: x => x.VpnEndpointId,
                        principalTable: "VpnEndpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VpnEndpointSources_VpnSources_VpnSourceId",
                        column: x => x.VpnSourceId,
                        principalTable: "VpnSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VpnEndpoints_FirstSourceId",
                table: "VpnEndpoints",
                column: "FirstSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnEndpoints_Host_Port_Protocol_Transport",
                table: "VpnEndpoints",
                columns: new[] { "Host", "Port", "Protocol", "Transport" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VpnEndpoints_LastSeenAt",
                table: "VpnEndpoints",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_VpnEndpoints_Status_NextCheckAt",
                table: "VpnEndpoints",
                columns: new[] { "Status", "NextCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VpnEndpointSources_VpnSourceId",
                table: "VpnEndpointSources",
                column: "VpnSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VpnSources_Enabled_Priority",
                table: "VpnSources",
                columns: new[] { "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_VpnSources_Url",
                table: "VpnSources",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VpnEndpointSources");

            migrationBuilder.DropTable(
                name: "VpnEndpoints");

            migrationBuilder.DropTable(
                name: "VpnSources");
        }
    }
}
