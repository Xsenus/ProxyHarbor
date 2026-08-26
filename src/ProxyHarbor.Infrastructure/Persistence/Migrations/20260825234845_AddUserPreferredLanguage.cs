using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferredLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "AspNetUsers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "ru");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_PreferredLanguage",
                table: "AspNetUsers",
                sql: "\"PreferredLanguage\" IN ('ru', 'en', 'de', 'fr', 'zh')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_PreferredLanguage",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "AspNetUsers");
        }
    }
}
