using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenRequestHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserApiTokenRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserApiTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Query = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserApiTokenRequests", x => x.Id);
                    table.CheckConstraint("CK_UserApiTokenRequests_Duration", "\"DurationMs\" >= 0");
                    table.CheckConstraint("CK_UserApiTokenRequests_ItemCount", "\"ItemCount\" IS NULL OR \"ItemCount\" >= 0");
                    table.CheckConstraint("CK_UserApiTokenRequests_Status", "\"StatusCode\" BETWEEN 100 AND 599");
                    table.ForeignKey(
                        name: "FK_UserApiTokenRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserApiTokenRequests_UserApiTokens_UserApiTokenId",
                        column: x => x.UserApiTokenId,
                        principalTable: "UserApiTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserApiTokenRequests_UserApiTokenId_RequestedAt",
                table: "UserApiTokenRequests",
                columns: new[] { "UserApiTokenId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserApiTokenRequests_UserId_RequestedAt",
                table: "UserApiTokenRequests",
                columns: new[] { "UserId", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserApiTokenRequests");
        }
    }
}
