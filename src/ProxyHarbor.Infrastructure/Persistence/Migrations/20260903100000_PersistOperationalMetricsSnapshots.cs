using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProxyHarbor.Infrastructure.Persistence.Migrations;

/// <summary>
/// Сохраняет последние компактные proxy/VPN metrics snapshots, чтобы restart API
/// не блокировал первый пользовательский запрос полным чтением больших таблиц.
/// </summary>
[DbContext(typeof(ProxyHarborDbContext))]
[Migration("20260903100000_PersistOperationalMetricsSnapshots")]
public sealed class PersistOperationalMetricsSnapshots : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MetricsSnapshotStates",
            columns: table => new
            {
                Key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MetricsSnapshotStates", x => x.Key);
                table.CheckConstraint(
                    "CK_MetricsSnapshotStates_Key",
                    "\"Key\" IN ('proxy', 'vpn')");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "MetricsSnapshotStates");
}
