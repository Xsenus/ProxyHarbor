using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Сводное состояние канонического каталога, не смешанное с пользовательскими feed'ами.</summary>
public sealed record SourceCatalogSnapshot(
    int ExpectedSources,
    int PresentSources,
    int EnabledSources,
    int HealthySources,
    int FailingSources,
    int NeverAuditedSources,
    int ExpectedProviders,
    int PresentProviders,
    int EnabledProviders,
    bool IsComplete,
    bool IsHealthy);

/// <summary>Единообразно рассчитывает completeness и operational health встроенных источников.</summary>
public static class SourceCatalogHealth
{
    /// <summary>Строит снимок только по точным URL из версионируемого встроенного каталога.</summary>
    public static SourceCatalogSnapshot Calculate(IEnumerable<ProxySource> sources)
    {
        // URL уникален в PostgreSQL, но словарь делает функцию детерминированной и для
        // произвольных тестовых/внешних коллекций с ошибочными дубликатами.
        var matched = new Dictionary<string, (ProxySource Source, BuiltInSource Definition)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var definition = BuiltInSourceCatalog.FindByUrl(source.Url);
            if (definition is not null) matched.TryAdd(definition.Url, (source, definition));
        }

        var entries = matched.Values.ToArray();
        var enabled = entries.Where(entry => entry.Source.Enabled).ToArray();
        var healthy = enabled.Count(entry =>
            entry.Source.LastFetchedAt is not null &&
            entry.Source.LastSucceededAt is not null &&
            entry.Source.LastItemCount > 0 &&
            entry.Source.ConsecutiveFailures == 0 &&
            string.IsNullOrWhiteSpace(entry.Source.LastError));
        var failing = enabled.Count(entry =>
            entry.Source.ConsecutiveFailures > 0 || !string.IsNullOrWhiteSpace(entry.Source.LastError));
        var neverAudited = enabled.Count(entry => entry.Source.LastFetchedAt is null);
        var presentProviders = entries.Select(entry => entry.Definition.Provider)
            .Distinct(StringComparer.Ordinal).Count();
        var enabledProviders = enabled.Select(entry => entry.Definition.Provider)
            .Distinct(StringComparer.Ordinal).Count();
        var expectedSources = BuiltInSourceCatalog.Sources.Count;
        var expectedProviders = BuiltInSourceCatalog.ProviderCount;
        var complete = entries.Length == expectedSources && enabled.Length == expectedSources &&
            presentProviders == expectedProviders && enabledProviders == expectedProviders;

        return new SourceCatalogSnapshot(
            expectedSources,
            entries.Length,
            enabled.Length,
            healthy,
            failing,
            neverAudited,
            expectedProviders,
            presentProviders,
            enabledProviders,
            complete,
            complete && healthy == expectedSources);
    }
}
