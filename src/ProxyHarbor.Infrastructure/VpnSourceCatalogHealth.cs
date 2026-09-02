using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сводное состояние встроенного VPN-каталога без пользовательских feed.</summary>
public sealed record VpnSourceCatalogSnapshot(
    DateOnly LastAuditedOn,
    int ExpectedSources,
    int PresentSources,
    int EnabledSources,
    int HealthySources,
    int FailingSources,
    int NeverAuditedSources,
    int StaleSources,
    int ExpectedProviders,
    int PresentProviders,
    int EnabledProviders,
    bool IsComplete,
    bool IsHealthy);

/// <summary>Единообразно рассчитывает completeness и operational health встроенных VPN feed.</summary>
public static class VpnSourceCatalogHealth
{
    /// <summary>Строит снимок только по точным URL из версионируемого VPN-каталога.</summary>
    public static VpnSourceCatalogSnapshot Calculate(
        IEnumerable<VpnSource> sources,
        DateTimeOffset now,
        TimeSpan freshnessWindow)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessWindow, TimeSpan.Zero);
        var freshAfter = now.Subtract(freshnessWindow);
        var matched = new Dictionary<string, (VpnSource Source, VpnSourceDefinition Definition)>(
            StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var definition = BuiltInVpnSourceCatalog.FindByUrl(source.Url);
            if (definition is not null) matched.TryAdd(definition.Url, (source, definition));
        }

        var entries = matched.Values.ToArray();
        var enabled = entries.Where(entry => entry.Source.Enabled).ToArray();
        var healthy = enabled.Count(entry =>
            entry.Source.LastFetchedAt >= freshAfter &&
            entry.Source.LastSucceededAt >= freshAfter &&
            entry.Source.LastItemCount > 0 &&
            entry.Source.ConsecutiveFailures == 0 &&
            string.IsNullOrWhiteSpace(entry.Source.LastError));
        var failing = enabled.Count(entry =>
            entry.Source.ConsecutiveFailures > 0 || !string.IsNullOrWhiteSpace(entry.Source.LastError));
        var neverAudited = enabled.Count(entry => entry.Source.LastFetchedAt is null);
        var stale = enabled.Count(entry =>
            entry.Source.LastFetchedAt is not null && entry.Source.LastFetchedAt < freshAfter);
        var presentProviders = entries.Select(entry => entry.Definition.Provider)
            .Distinct(StringComparer.Ordinal).Count();
        var enabledProviders = enabled.Select(entry => entry.Definition.Provider)
            .Distinct(StringComparer.Ordinal).Count();
        var expectedSources = BuiltInVpnSourceCatalog.Sources.Count;
        var expectedProviders = BuiltInVpnSourceCatalog.ProviderCount;
        var complete = entries.Length == expectedSources && enabled.Length == expectedSources &&
            presentProviders == expectedProviders && enabledProviders == expectedProviders;

        return new VpnSourceCatalogSnapshot(
            BuiltInVpnSourceCatalog.LastAuditedOn,
            expectedSources,
            entries.Length,
            enabled.Length,
            healthy,
            failing,
            neverAudited,
            stale,
            expectedProviders,
            presentProviders,
            enabledProviders,
            complete,
            complete && healthy == expectedSources);
    }
}
