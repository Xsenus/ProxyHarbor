using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует полноту и runtime-здоровье встроенного VPN-каталога.</summary>
public sealed class VpnSourceCatalogHealthTests
{
    private static readonly DateTimeOffset AuditNow =
        new(2026, 9, 2, 12, 15, 0, TimeSpan.Zero);
    private static readonly TimeSpan FreshnessWindow = SourceCatalogHealth.FreshnessWindow(15);

    [Fact]
    public void CompleteAuditedCatalogIsHealthyAndIgnoresCustomFeeds()
    {
        var sources = HealthyCatalog();
        sources.Add(new VpnSource
        {
            Name = "custom failed",
            Provider = "Custom",
            Url = "https://example.com/custom-vpn.txt",
            DefaultProtocol = VpnProtocol.Vless,
            License = "custom",
            LastError = "timeout",
            ConsecutiveFailures = 5
        });

        var snapshot = VpnSourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(174, snapshot.ExpectedSources);
        Assert.Equal(new DateOnly(2026, 9, 2), snapshot.LastAuditedOn);
        Assert.Equal(174, snapshot.PresentSources);
        Assert.Equal(174, snapshot.EnabledSources);
        Assert.Equal(174, snapshot.HealthySources);
        Assert.Equal(32, snapshot.ExpectedProviders);
        Assert.Equal(32, snapshot.PresentProviders);
        Assert.True(snapshot.IsComplete);
        Assert.True(snapshot.IsHealthy);
    }

    [Fact]
    public void DisabledBuiltInFeedMakesCatalogIncomplete()
    {
        var sources = HealthyCatalog();
        sources[0].Enabled = false;

        var snapshot = VpnSourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(174, snapshot.PresentSources);
        Assert.Equal(173, snapshot.EnabledSources);
        Assert.Equal(173, snapshot.HealthySources);
        Assert.False(snapshot.IsComplete);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void MissingProviderAndStaleFailureAreReportedIndependently()
    {
        var sources = HealthyCatalog();
        var singleFeedProvider = BuiltInVpnSourceCatalog.Sources
            .GroupBy(source => source.Provider)
            .First(group => group.Count() == 1).Single();
        sources.RemoveAll(source => source.Url == singleFeedProvider.Url);
        sources[0].LastFetchedAt = AuditNow.Subtract(FreshnessWindow).AddTicks(-1);
        sources[0].LastSucceededAt = sources[0].LastFetchedAt;
        sources[1].ConsecutiveFailures = 1;
        sources[1].LastError = "timeout";

        var snapshot = VpnSourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(173, snapshot.PresentSources);
        Assert.Equal(31, snapshot.PresentProviders);
        Assert.Equal(1, snapshot.StaleSources);
        Assert.Equal(1, snapshot.FailingSources);
        Assert.Equal(171, snapshot.HealthySources);
        Assert.False(snapshot.IsComplete);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void NeverAuditedFeedIsNotHealthy()
    {
        var sources = HealthyCatalog();
        sources[0].LastFetchedAt = null;
        sources[0].LastSucceededAt = null;
        sources[0].LastItemCount = 0;

        var snapshot = VpnSourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(1, snapshot.NeverAuditedSources);
        Assert.Equal(173, snapshot.HealthySources);
        Assert.False(snapshot.IsHealthy);
    }

    private static List<VpnSource> HealthyCatalog()
    {
        var auditedAt = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        return BuiltInVpnSourceCatalog.Sources.Select(definition => new VpnSource
        {
            Name = definition.Name,
            Provider = definition.Provider,
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            License = definition.License,
            Enabled = true,
            LastFetchedAt = auditedAt,
            LastSucceededAt = auditedAt,
            LastItemCount = 1
        }).ToList();
    }
}
