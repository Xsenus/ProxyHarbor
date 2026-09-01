namespace ProxyHarbor.Api;

/// <summary>Общие имена и предикаты политик кэширования публичной выдачи.</summary>
internal static class PublicOutputCachePolicies
{
    internal const string ProxyCatalog = "public-proxy-catalog";
    internal const string VpnCatalog = "public-vpn-catalog";
    internal const string Countries = "public-countries";
    internal const string Summary = "public-summary";
    internal const string Metrics = "prometheus-metrics";
    internal const string SeekFirstPage = "public-seek-first-page";
    internal static readonly TimeSpan CatalogExpiration = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SummaryExpiration = TimeSpan.FromSeconds(15);
    // Типичный Prometheus scrape выполняется каждые 15 секунд. Более короткий либо
    // равный TTL превращал почти каждый scrape в полный scan большого каталога.
    internal static readonly TimeSpan MetricsExpiration = TimeSpan.FromSeconds(30);
    internal static readonly string[] ListVaryByQuery =
        ["protocol", "maxLatencyMs", "minSuccessRate", "country", "page", "pageSize"];
    internal static readonly string[] VpnListVaryByQuery =
        ["page", "pageSize", "protocol", "status", "country"];
    internal static readonly string[] SeekVaryByQuery =
        ["protocol", "maxLatencyMs", "minSuccessRate", "country", "pageSize"];

    /// <summary>
    /// Кэшируется только начало keyset-обхода: оно одинаково для многих клиентов.
    /// Continuation cursor почти всегда уникален и не должен вытеснять горячие ответы
    /// из ограниченного in-memory output cache.
    /// </summary>
    internal static bool IsSeekFirstPage(HttpContext context) =>
        !context.Request.Query.ContainsKey("after");

    /// <summary>
    /// Каталог зависит от подписки. В общий cache допускаются только запросы без
    /// cookie/API-token principal; ASP.NET default policy остаётся дополнительной защитой.
    /// </summary>
    internal static bool IsAnonymous(HttpContext context) =>
        context.User.Identity?.IsAuthenticated != true;

    /// <summary>Локализованные сообщения и Content-Language не смешиваются в одном ключе.</summary>
    internal static KeyValuePair<string, string> CultureKey(HttpContext _) =>
        new("ui-culture", System.Globalization.CultureInfo.CurrentUICulture.Name);
}
