using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionReminderBatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Reminder12HoursForExpiresAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Reminder1HourForExpiresAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Active_ExpiresAt_Id",
                table: "Subscriptions",
                columns: new[] { "ExpiresAt", "Id" },
                filter: "\"Status\" = 'active' AND \"ExpiresAt\" IS NOT NULL")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_Active_ExpiresAt_Id",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Reminder12HoursForExpiresAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Reminder1HourForExpiresAt",
                table: "Subscriptions");
        }
    }
}
