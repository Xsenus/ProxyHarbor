using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramMarketingConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MarketingConsentGrantedAt",
                table: "TelegramChats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketingConsentVersion",
                table: "TelegramChats",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MarketingConsentWithdrawnAt",
                table: "TelegramChats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MarketingNotificationsEnabled",
                table: "TelegramChats",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChats_MarketingNotificationsEnabled_IsBlocked_LastI~",
                table: "TelegramChats",
                columns: new[] { "MarketingNotificationsEnabled", "IsBlocked", "LastInteractionAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelegramChats_MarketingNotificationsEnabled_IsBlocked_LastI~",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "MarketingConsentGrantedAt",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "MarketingConsentVersion",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "MarketingConsentWithdrawnAt",
                table: "TelegramChats");

            migrationBuilder.DropColumn(
                name: "MarketingNotificationsEnabled",
                table: "TelegramChats");
        }
    }
}
