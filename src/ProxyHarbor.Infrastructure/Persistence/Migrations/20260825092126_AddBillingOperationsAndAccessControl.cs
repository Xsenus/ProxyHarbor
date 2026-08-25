using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingOperationsAndAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Subscriptions_Status",
                table: "Subscriptions");

            migrationBuilder.CreateTable(
                name: "AccessBlockRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessBlockRules", x => x.Id);
                    table.CheckConstraint("CK_AccessBlockRules_Kind", "\"Kind\" IN ('ip', 'cidr', 'user')");
                    table.ForeignKey(
                        name: "FK_AccessBlockRules_AspNetUsers_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessBlockRules_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxyAccessBuckets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BucketStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Requests = table.Column<int>(type: "integer", nullable: false),
                    BlockedRequests = table.Column<int>(type: "integer", nullable: false),
                    ProxyItems = table.Column<long>(type: "bigint", nullable: false),
                    BytesSent = table.Column<long>(type: "bigint", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyAccessBuckets", x => x.Id);
                    table.CheckConstraint("CK_ProxyAccessBuckets_Counters", "\"Requests\" >= 0 AND \"BlockedRequests\" >= 0 AND \"ProxyItems\" >= 0 AND \"BytesSent\" >= 0");
                    table.ForeignKey(
                        name: "FK_ProxyAccessBuckets_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionAdminActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviousPlan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviousExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NewPlan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NewExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionAdminActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionAdminActions_AspNetUsers_AdministratorId",
                        column: x => x.AdministratorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionAdminActions_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscriptions_Status",
                table: "Subscriptions",
                sql: "\"Status\" IN ('active', 'trialing', 'past_due', 'canceled', 'expired', 'suspended')");

            migrationBuilder.CreateIndex(
                name: "IX_AccessBlockRules_AdministratorId",
                table: "AccessBlockRules",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessBlockRules_Enabled_ExpiresAt",
                table: "AccessBlockRules",
                columns: new[] { "Enabled", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessBlockRules_Kind_Value",
                table: "AccessBlockRules",
                columns: new[] { "Kind", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessBlockRules_UserId",
                table: "AccessBlockRules",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyAccessBuckets_BucketStartedAt_IpAddress_UserId_Endpoint",
                table: "ProxyAccessBuckets",
                columns: new[] { "BucketStartedAt", "IpAddress", "UserId", "Endpoint" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_ProxyAccessBuckets_LastSeenAt_Requests",
                table: "ProxyAccessBuckets",
                columns: new[] { "LastSeenAt", "Requests" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxyAccessBuckets_UserId",
                table: "ProxyAccessBuckets",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionAdminActions_AdministratorId",
                table: "SubscriptionAdminActions",
                column: "AdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionAdminActions_SubscriptionId_CreatedAt",
                table: "SubscriptionAdminActions",
                columns: new[] { "SubscriptionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessBlockRules");

            migrationBuilder.DropTable(
                name: "ProxyAccessBuckets");

            migrationBuilder.DropTable(
                name: "SubscriptionAdminActions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Subscriptions_Status",
                table: "Subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscriptions_Status",
                table: "Subscriptions",
                sql: "\"Status\" IN ('active', 'trialing', 'past_due', 'canceled', 'expired')");
        }
    }
}
