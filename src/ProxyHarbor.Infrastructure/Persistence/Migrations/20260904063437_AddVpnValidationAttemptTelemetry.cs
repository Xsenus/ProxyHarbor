using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVpnValidationAttemptTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastValidationAttemptAt",
                table: "VpnEndpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastValidationDeferred",
                table: "VpnEndpoints",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VpnEndpoints_DeferredAttempt",
                table: "VpnEndpoints",
                sql: "NOT \"LastValidationDeferred\" OR \"LastValidationAttemptAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VpnEndpoints_DeferredAttempt",
                table: "VpnEndpoints");

            migrationBuilder.DropColumn(
                name: "LastValidationAttemptAt",
                table: "VpnEndpoints");

            migrationBuilder.DropColumn(
                name: "LastValidationDeferred",
                table: "VpnEndpoints");
        }
    }
}
