using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramBotConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProtectedSecrets = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    BotId = table.Column<long>(type: "bigint", nullable: true),
                    BotUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProvisionedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramBotConfigurations", x => x.Id);
                    table.CheckConstraint("CK_TelegramBotConfigurations_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "TelegramChats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    NotificationsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastInteractionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramChats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramChats_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramUpdateReceipts",
                columns: table => new
                {
                    UpdateId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUpdateReceipts", x => x.UpdateId);
                    table.CheckConstraint("CK_TelegramUpdateReceipts_Transport", "\"Transport\" IN ('webhook', 'polling')");
                });

            migrationBuilder.CreateTable(
                name: "TelegramOutboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TelegramChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramOutboundMessages", x => x.Id);
                    table.CheckConstraint("CK_TelegramOutboundMessages_Attempts", "\"Attempts\" BETWEEN 0 AND 20");
                    table.CheckConstraint("CK_TelegramOutboundMessages_Kind", "\"Kind\" IN ('text', 'invoice', 'proxy_file')");
                    table.CheckConstraint("CK_TelegramOutboundMessages_Status", "\"Status\" IN ('pending', 'processing', 'sent', 'failed', 'canceled')");
                    table.ForeignKey(
                        name: "FK_TelegramOutboundMessages_TelegramChats_TelegramChatId",
                        column: x => x.TelegramChatId,
                        principalTable: "TelegramChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TelegramChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboundMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramConversationMessages", x => x.Id);
                    table.CheckConstraint("CK_TelegramConversationMessages_Direction", "\"Direction\" IN ('inbound', 'bot', 'admin')");
                    table.ForeignKey(
                        name: "FK_TelegramConversationMessages_AspNetUsers_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TelegramConversationMessages_TelegramChats_TelegramChatId",
                        column: x => x.TelegramChatId,
                        principalTable: "TelegramChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TelegramConversationMessages_TelegramOutboundMessages_Outbo~",
                        column: x => x.OutboundMessageId,
                        principalTable: "TelegramOutboundMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChats_ChatId",
                table: "TelegramChats",
                column: "ChatId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChats_NotificationsEnabled_IsBlocked_LastInteractio~",
                table: "TelegramChats",
                columns: new[] { "NotificationsEnabled", "IsBlocked", "LastInteractionAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChats_TelegramUserId",
                table: "TelegramChats",
                column: "TelegramUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChats_UserId",
                table: "TelegramChats",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramConversationMessages_AdministratorId",
                table: "TelegramConversationMessages",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramConversationMessages_OutboundMessageId",
                table: "TelegramConversationMessages",
                column: "OutboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramConversationMessages_TelegramChatId_CreatedAt",
                table: "TelegramConversationMessages",
                columns: new[] { "TelegramChatId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramOutboundMessages_IdempotencyKey",
                table: "TelegramOutboundMessages",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramOutboundMessages_Status_AvailableAt_LeaseUntil",
                table: "TelegramOutboundMessages",
                columns: new[] { "Status", "AvailableAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramOutboundMessages_TelegramChatId_CreatedAt",
                table: "TelegramOutboundMessages",
                columns: new[] { "TelegramChatId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdateReceipts_ReceivedAt",
                table: "TelegramUpdateReceipts",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramBotConfigurations");

            migrationBuilder.DropTable(
                name: "TelegramConversationMessages");

            migrationBuilder.DropTable(
                name: "TelegramUpdateReceipts");

            migrationBuilder.DropTable(
                name: "TelegramOutboundMessages");

            migrationBuilder.DropTable(
                name: "TelegramChats");
        }
    }
}
