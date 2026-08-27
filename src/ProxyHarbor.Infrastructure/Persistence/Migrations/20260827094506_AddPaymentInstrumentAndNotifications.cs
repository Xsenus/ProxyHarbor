using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentInstrumentAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentInstrument",
                table: "PaymentOrders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "PaymentOrders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "PaymentOrders"
                SET "PaymentMethod" = CASE
                    WHEN "Provider" = 'telegram_stars' THEN 'telegram_stars'
                    WHEN "Provider" = 'yoomoney' THEN 'wallet'
                    WHEN "Provider" IN ('cryptomus', 'nowpayments') THEN 'crypto'
                    ELSE 'payment_gateway'
                END,
                "PaymentInstrument" = CASE
                    WHEN "Provider" = 'telegram_stars' THEN 'Telegram Stars'
                    ELSE "Provider"
                END;
                """);

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActionUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_DeduplicationKey",
                table: "UserNotifications",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId_DeliveredAt_CreatedAt",
                table: "UserNotifications",
                columns: new[] { "UserId", "DeliveredAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "PaymentInstrument",
                table: "PaymentOrders");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "PaymentOrders");
        }
    }
}
