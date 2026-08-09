using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует различие между полнотой каталога и результатом последнего сетевого аудита.</summary>
public sealed class SourceCatalogHealthTests
{
    private static readonly DateTimeOffset AuditNow =
        new(2026, 8, 9, 12, 15, 0, TimeSpan.Zero);
    private static readonly TimeSpan FreshnessWindow = SourceCatalogHealth.FreshnessWindow(15);

    [Fact]
    public void CompleteAuditedCatalogIsHealthyAndIgnoresCustomFeeds()
    {
        var sources = HealthyCatalog();
        sources.Add(new ProxySource
        {
            Name = "custom failed",
            Url = "https://example.com/custom.txt",
            DefaultProtocol = ProxyProtocol.Http,
            LastError = "timeout",
            ConsecutiveFailures = 5
        });

        var snapshot = SourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(81, snapshot.ExpectedSources);
        Assert.Equal(new DateOnly(2026, 8, 9), snapshot.LastAuditedOn);
        Assert.Equal(81, snapshot.PresentSources);
        Assert.Equal(81, snapshot.EnabledSources);
        Assert.Equal(81, snapshot.HealthySources);
        Assert.Equal(50, snapshot.ExpectedProviders);
        Assert.Equal(50, snapshot.PresentProviders);
        Assert.True(snapshot.IsComplete);
        Assert.True(snapshot.IsHealthy);
        Assert.Equal(0, snapshot.StaleSources);
    }

    [Fact]
    public void DisabledBuiltInFeedMakesCatalogIncompleteWithoutCallingItFailed()
    {
        var sources = HealthyCatalog();
        sources[0].Enabled = false;

        var snapshot = SourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(81, snapshot.PresentSources);
        Assert.Equal(80, snapshot.EnabledSources);
        Assert.Equal(80, snapshot.HealthySources);
        Assert.Equal(0, snapshot.FailingSources);
        Assert.False(snapshot.IsComplete);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void MissingSingleProviderAndNeverAuditedFeedAreReportedSeparately()
    {
        var sources = HealthyCatalog();
        var singleFeedProvider = BuiltInSourceCatalog.Sources
            .GroupBy(source => source.ProviderIdentity)
            .First(group => group.Count() == 1).Single();
        sources.RemoveAll(source => source.Url == singleFeedProvider.Url);
        sources[0].LastFetchedAt = null;
        sources[0].LastSucceededAt = null;
        sources[0].LastItemCount = 0;

        var snapshot = SourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.Equal(80, snapshot.PresentSources);
        Assert.Equal(49, snapshot.PresentProviders);
        Assert.Equal(1, snapshot.NeverAuditedSources);
        Assert.Equal(79, snapshot.HealthySources);
        Assert.False(snapshot.IsComplete);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void HistoricallySuccessfulButStaleFeedMakesCatalogUnhealthy()
    {
        var sources = HealthyCatalog();
        sources[0].LastFetchedAt = AuditNow.Subtract(FreshnessWindow).AddTicks(-1);
        sources[0].LastSucceededAt = sources[0].LastFetchedAt;

        var snapshot = SourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(1, snapshot.StaleSources);
        Assert.Equal(80, snapshot.HealthySources);
        Assert.Equal(0, snapshot.FailingSources);
        Assert.Equal(0, snapshot.NeverAuditedSources);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void FreshButTruncatedFeedMakesCatalogUnhealthyWithoutCallingItFailed()
    {
        var sources = HealthyCatalog();
        sources[0].LastResultTruncated = true;

        var snapshot = SourceCatalogHealth.Calculate(sources, AuditNow, FreshnessWindow);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(1, snapshot.TruncatedSources);
        Assert.Equal(80, snapshot.HealthySources);
        Assert.Equal(0, snapshot.FailingSources);
        Assert.False(snapshot.IsHealthy);
    }

    private static List<ProxySource> HealthyCatalog()
    {
        var auditedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        return BuiltInSourceCatalog.Sources.Select(definition => new ProxySource
        {
            Name = definition.Name,
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            Enabled = true,
            LastFetchedAt = auditedAt,
            LastSucceededAt = auditedAt,
            LastItemCount = 1
        }).ToList();
    }
}
