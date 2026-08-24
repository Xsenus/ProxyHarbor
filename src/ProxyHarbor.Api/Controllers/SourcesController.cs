using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Публично описывает встроенные источники без раскрытия эксплуатационного состояния.</summary>
[ApiController, Route("api/v1/sources"), EnableRateLimiting("public")]
public sealed class SourcesController : ControllerBase
{
    // Каталог неизменяем в рамках процесса, поэтому агрегируем его один раз, а не на каждый запрос.
    private static readonly PublicSourceCatalogResponse Catalog = CreateCatalog();

    /// <summary>Возвращает 56 независимых провайдеров и все их встроенные feed-endpoint.</summary>
    [HttpGet]
    [OutputCache(PolicyName = "public-summary")]
    public ActionResult<PublicSourceCatalogResponse> Get() => Ok(Catalog);

    private static PublicSourceCatalogResponse CreateCatalog()
    {
        var providers = BuiltInSourceCatalog.Sources
            .GroupBy(source => source.ProviderIdentity, StringComparer.Ordinal)
            .OrderBy(group => group.Min(source => source.Rank))
            .Select((group, providerIndex) =>
            {
                var feeds = group.Select(source => new PublicSourceFeedResponse(
                    source.Rank, source.Name, source.Url, source.Protocol)).ToArray();
                return new PublicSourceProviderResponse(
                    providerIndex + 1,
                    group.First().Provider,
                    feeds.Select(feed => feed.Protocol).Distinct().ToArray(),
                    feeds);
            })
            .ToArray();
        return new PublicSourceCatalogResponse(
            BuiltInSourceCatalog.LastAuditedOn,
            BuiltInSourceCatalog.Sources.Count,
            providers.Length,
            providers);
    }
}

/// <summary>Стабильный публичный снимок встроенного source-каталога.</summary>
public sealed record PublicSourceCatalogResponse(
    DateOnly LastAuditedOn,
    int FeedCount,
    int ProviderCount,
    IReadOnlyList<PublicSourceProviderResponse> Providers);

/// <summary>Один независимый владелец одной или нескольких протокольных лент.</summary>
public sealed record PublicSourceProviderResponse(
    int Rank,
    string Name,
    IReadOnlyList<ProxyProtocol> Protocols,
    IReadOnlyList<PublicSourceFeedResponse> Feeds);

/// <summary>Публичные неизменяемые метаданные одного встроенного endpoint.</summary>
public sealed record PublicSourceFeedResponse(int Rank, string Name, string Url, ProxyProtocol Protocol);
