namespace ProxyHarbor.Domain;

/// <summary>Heartbeat внешнего checker-agent без чувствительных данных.</summary>
public sealed record CheckerHeartbeatRequest(string Version, int ActiveChecks = 0, string? Error = null);

/// <summary>Минимальная неизменяемая запись прокси, передаваемая внешнему checker-agent.</summary>
public sealed record CheckerProxyItem(Guid Id, string Host, int Port, ProxyProtocol Protocol);

/// <summary>Атомарно арендованная партия и параметры объективной HTTPS-пробы.</summary>
public sealed record CheckerLeaseResponse(
    Guid LeaseId,
    DateTimeOffset LeaseUntil,
    int Concurrency,
    int ProbeTimeoutSeconds,
    string ProbeHost,
    int ProbePort,
    string ProbePath,
    IReadOnlyList<CheckerProxyItem> Items);

/// <summary>Объективный результат одной проверки, возвращаемый внешним агентом.</summary>
public sealed record CheckerProxyResult(
    Guid ProxyId,
    bool IsAlive,
    int? LatencyMs,
    string? ExitIp,
    bool IsAnonymous,
    string? Error,
    bool IsDeferred = false);

/// <summary>Полный результат партии. Частичные партии центральный сервис не принимает.</summary>
public sealed record CheckerLeaseResultRequest(IReadOnlyList<CheckerProxyResult> Results);

/// <summary>Подтверждённые центральной БД счётчики завершённой партии.</summary>
public sealed record CheckerLeaseCompletion(int Checked, int Alive, int Deferred);
