using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Не допускает попадания runtime-секретов в Telegram backup.</summary>
public sealed class BackupRuntimeSettingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SnapshotKeepsNetworkSettingsButNeverSecretValues()
    {
        const string adminKey = "admin-secret-must-not-be-serialized";
        const string connection = "Host=db;Password=database-secret";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:Origins:0"] = "https://dashboard.example",
            ["ForwardedHeaders:KnownNetworks:0"] = "172.30.0.0/24",
            ["Security:AdminApiKey"] = adminKey,
            ["ConnectionStrings:Postgres"] = connection
        }).Build();

        var snapshot = BackupRuntimeSettings.FromConfiguration(configuration);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        Assert.Equal(["https://dashboard.example"], snapshot.CorsOrigins);
        Assert.Equal(["172.30.0.0/24"], snapshot.ForwardedHeaderKnownNetworks);
        Assert.True(snapshot.AdminApiKeyConfigured);
        Assert.False(snapshot.AdminApiKeyIncluded);
        Assert.False(snapshot.ConnectionStringIncluded);
        Assert.DoesNotContain(adminKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(connection, json, StringComparison.Ordinal);
        Assert.DoesNotContain("database-secret", json, StringComparison.Ordinal);
    }
}
