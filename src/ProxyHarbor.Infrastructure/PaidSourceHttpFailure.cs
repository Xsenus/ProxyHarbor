using System.Net;

namespace ProxyHarbor.Infrastructure;

/// <summary>Классифицирует HTTP-отказ без выдуманного срока действия ключа.</summary>
internal sealed record PaidSourceHttpFailure(string Status, string Message)
{
    internal static PaidSourceHttpFailure From(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new("invalid", "Ключ платного источника недействителен."),
        HttpStatusCode.Forbidden => new("error",
            "Платный источник отказал в доступе (HTTP 403). Проверьте ключ, разрешения и ограничения IP у provider; срок действия не подтверждён."),
        HttpStatusCode.TooManyRequests => new("rate_limited", "Платный provider временно ограничил частоту запросов."),
        _ => new("error", "Платный provider временно недоступен.")
    };
}
