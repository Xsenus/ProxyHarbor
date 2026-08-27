using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralProgram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            // Backfill existing accounts before the unique index is created.
            migrationBuilder.Sql(
                "UPDATE \"AspNetUsers\" SET \"ReferralCode\" = lower(substring(md5(\"Id\"::text), 1, 12)) WHERE \"ReferralCode\" = '';"
            );

            migrationBuilder.CreateTable(
                name: "ReferralRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferrerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferredUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralRelationships", x => x.Id);
                    table.CheckConstraint("CK_ReferralRelationships_DifferentUsers", "\"ReferrerUserId\" <> \"ReferredUserId\"");
                    table.CheckConstraint("CK_ReferralRelationships_Slot", "\"Slot\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_ReferralRelationships_AspNetUsers_ReferredUserId",
                        column: x => x.ReferredUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReferralRelationships_AspNetUsers_ReferrerUserId",
                        column: x => x.ReferrerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReferralRewards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferralRelationshipId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    RewardKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DaysGranted = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralRewards", x => x.Id);
                    table.CheckConstraint("CK_ReferralRewards_Days", "\"DaysGranted\" BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_ReferralRewards_Kind", "\"Kind\" IN ('signup', 'purchase')");
                    table.ForeignKey(
                        name: "FK_ReferralRewards_PaymentOrders_PaymentOrderId",
                        column: x => x.PaymentOrderId,
                        principalTable: "PaymentOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReferralRewards_ReferralRelationships_ReferralRelationshipId",
                        column: x => x.ReferralRelationshipId,
                        principalTable: "ReferralRelationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ReferralCode",
                table: "AspNetUsers",
                column: "ReferralCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_ReferredUserId",
                table: "ReferralRelationships",
                column: "ReferredUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_ReferrerUserId_CreatedAt",
                table: "ReferralRelationships",
                columns: new[] { "ReferrerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_ReferrerUserId_Slot",
                table: "ReferralRelationships",
                columns: new[] { "ReferrerUserId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_PaymentOrderId",
                table: "ReferralRewards",
                column: "PaymentOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_ReferralRelationshipId_CreatedAt",
                table: "ReferralRewards",
                columns: new[] { "ReferralRelationshipId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_RewardKey",
                table: "ReferralRewards",
                column: "RewardKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferralRewards");

            migrationBuilder.DropTable(
                name: "ReferralRelationships");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ReferralCode",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "AspNetUsers");
        }
    }
}
