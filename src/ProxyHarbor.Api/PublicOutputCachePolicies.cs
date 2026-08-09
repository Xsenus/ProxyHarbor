namespace ProxyHarbor.Api;

/// <summary>Общие имена и предикаты политик кэширования публичной выдачи.</summary>
internal static class PublicOutputCachePolicies
{
    internal const string SeekFirstPage = "public-seek-first-page";

    /// <summary>
    /// Кэшируется только начало keyset-обхода: оно одинаково для многих клиентов.
    /// Continuation cursor почти всегда уникален и не должен вытеснять горячие ответы
    /// из ограниченного in-memory output cache.
    /// </summary>
    internal static bool IsSeekFirstPage(HttpContext context) =>
        !context.Request.Query.ContainsKey("after");
}
