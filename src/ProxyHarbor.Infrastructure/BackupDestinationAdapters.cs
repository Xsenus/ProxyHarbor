using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Операция, которую orchestration вправе запросить у backup destination.</summary>
public enum BackupDestinationOperation
{
    /// <summary>Записать immutable ciphertext.</summary>
    Put,
    /// <summary>Независимо подтвердить существование и целостность ранее записанного объекта.</summary>
    Verify,
    /// <summary>Получить объект для локального restore.</summary>
    Materialize
}

/// <summary>Класс безопасно отображаемой ошибки provider adapter.</summary>
public enum BackupDestinationErrorCode
{
    /// <summary>Операция не объявлена adapter.</summary>
    UnsupportedOperation,
    /// <summary>Конфигурация отсутствует либо не проходит allowlist validation.</summary>
    InvalidConfiguration,
    /// <summary>Provider отклонил credentials.</summary>
    AuthenticationFailed,
    /// <summary>Credentials не имеют необходимого минимального права.</summary>
    AuthorizationFailed,
    /// <summary>Provider ограничил частоту запросов.</summary>
    RateLimited,
    /// <summary>Исчерпана provider quota либо допустимый размер.</summary>
    QuotaExceeded,
    /// <summary>Операция не завершилась за bounded timeout.</summary>
    Timeout,
    /// <summary>Provider временно недоступен.</summary>
    Unavailable,
    /// <summary>Запрос мог быть применён, но подтверждение потеряно.</summary>
    UnknownOutcome,
    /// <summary>Размер или checksum не совпали с ожидаемыми.</summary>
    IntegrityMismatch,
    /// <summary>Immutable locator уже занят другим содержимым.</summary>
    Collision,
    /// <summary>Подтверждённое отсутствие объекта.</summary>
    NotFound,
    /// <summary>Иной безопасно классифицированный постоянный отказ provider.</summary>
    ProviderRejected
}

/// <summary>Решение о повторе, не зависящее от текста provider exception.</summary>
public enum BackupDestinationFailureDisposition
{
    /// <summary>Повтор с bounded backoff разрешён.</summary>
    Retryable,
    /// <summary>Автоматический повтор не исправит причину.</summary>
    Permanent,
    /// <summary>Сначала нужен reconcile, повторная запись может создать дубль.</summary>
    UnknownOutcome
}

/// <summary>Типизированная ошибка без credentials, URL и provider response body.</summary>
public sealed record BackupDestinationFailure(
    BackupDestinationErrorCode Code,
    BackupDestinationFailureDisposition Disposition);

/// <summary>Возможности одной операции adapter.</summary>
public sealed record BackupDestinationOperationCapability(
    bool Supported,
    long? MaximumBytes = null,
    int? MaximumParts = null);

/// <summary>Доказанные возможности adapter; false означает fail-closed запрет.</summary>
public sealed record BackupDestinationCapabilities(
    BackupDestinationOperationCapability Put,
    BackupDestinationOperationCapability Verify,
    BackupDestinationOperationCapability Materialize,
    bool SupportsConditionalCreate,
    bool ProvidesNativeVersion,
    bool ProvidesNativeChecksum);

/// <summary>
/// Metadata-контракт destination adapter. Сетевые операции добавляются поверх него
/// по мере отдельной проверки provider semantics; registry сам никогда не выполняет I/O.
/// </summary>
public interface IBackupDestinationAdapter
{
    /// <summary>Allowlisted stable kind, сохранённый в BackupDestination.</summary>
    string Kind { get; }
    /// <summary>Только доказанные operation-specific capabilities.</summary>
    BackupDestinationCapabilities Capabilities { get; }
}

/// <summary>Причина отказа control-plane routing до provider I/O.</summary>
public enum BackupDestinationRouteRejection
{
    /// <summary>Kind отсутствует в статическом allowlist.</summary>
    UnknownKind,
    /// <summary>Allowlisted adapter не зарегистрирован.</summary>
    MissingAdapter,
    /// <summary>Для одного kind зарегистрировано больше одного adapter.</summary>
    DuplicateAdapter,
    /// <summary>Destination отключён.</summary>
    DestinationDisabled,
    /// <summary>Route отсутствует, отключён либо указывает на другой destination.</summary>
    RouteForbidden,
    /// <summary>Draining route не принимает новые writes.</summary>
    RouteDraining,
    /// <summary>Операция не включена в route allowlist.</summary>
    OperationForbidden,
    /// <summary>Adapter не объявляет поддержку операции.</summary>
    UnsupportedOperation,
    /// <summary>Объект превышает доказанную capability.</summary>
    ObjectTooLarge
}

/// <summary>Безопасная typed-ошибка выбора destination без provider details.</summary>
public sealed class BackupDestinationRouteException(
    BackupDestinationRouteRejection rejection,
    string message) : InvalidOperationException(message)
{
    /// <summary>Стабильный machine-readable код.</summary>
    public BackupDestinationRouteRejection Rejection { get; } = rejection;
}

/// <summary>
/// Fail-closed registry: принимает только встроенные kinds и никогда не ищет
/// неразрешённый fallback за пределами переданного pool route.
/// </summary>
public sealed class BackupDestinationRegistry
{
    private static readonly HashSet<string> AllowedKinds = new(["s3", "telegram"], StringComparer.Ordinal);
    private readonly Dictionary<string, IBackupDestinationAdapter> adapters;

    /// <summary>Создаёт полный registry и отклоняет missing/duplicate/unknown registrations.</summary>
    public BackupDestinationRegistry(IEnumerable<IBackupDestinationAdapter> registeredAdapters)
    {
        ArgumentNullException.ThrowIfNull(registeredAdapters);
        var materialized = registeredAdapters.ToArray();
        var unknown = materialized.FirstOrDefault(adapter => !AllowedKinds.Contains(adapter.Kind));
        if (unknown is not null)
            throw Reject(BackupDestinationRouteRejection.UnknownKind,
                $"Backup destination kind '{unknown.Kind}' не разрешён.");

        var duplicate = materialized.GroupBy(adapter => adapter.Kind, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw Reject(BackupDestinationRouteRejection.DuplicateAdapter,
                $"Backup destination kind '{duplicate.Key}' зарегистрирован повторно.");

        adapters = materialized.ToDictionary(adapter => adapter.Kind, StringComparer.Ordinal);
        var missing = AllowedKinds.FirstOrDefault(kind => !adapters.ContainsKey(kind));
        if (missing is not null)
            throw Reject(BackupDestinationRouteRejection.MissingAdapter,
                $"Backup destination kind '{missing}' не зарегистрирован.");
    }

    /// <summary>Возвращает adapter только после проверки точного разрешённого route.</summary>
    public IBackupDestinationAdapter Resolve(
        BackupDestination destination,
        BackupPoolDestination route,
        BackupDestinationOperation operation,
        long contentLength)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        var adapter = GetRequired(destination.Kind);
        if (!destination.Enabled)
            throw Reject(BackupDestinationRouteRejection.DestinationDisabled,
                "Backup destination отключён.");
        if (route.BackupDestinationId != destination.Id || !route.Enabled)
            throw Reject(BackupDestinationRouteRejection.RouteForbidden,
                "Backup destination не разрешён этим pool route.");
        if (operation == BackupDestinationOperation.Put && route.Draining)
            throw Reject(BackupDestinationRouteRejection.RouteDraining,
                "Draining backup route не принимает новые записи.");
        if (!RouteAllows(route.AllowedOperations, operation))
            throw Reject(BackupDestinationRouteRejection.OperationForbidden,
                "Операция не разрешена этим pool route.");

        var capability = operation switch
        {
            BackupDestinationOperation.Put => adapter.Capabilities.Put,
            BackupDestinationOperation.Verify => adapter.Capabilities.Verify,
            BackupDestinationOperation.Materialize => adapter.Capabilities.Materialize,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (!capability.Supported)
            throw Reject(BackupDestinationRouteRejection.UnsupportedOperation,
                "Backup adapter не поддерживает запрошенную операцию.");
        if (capability.MaximumBytes is { } maximumBytes && contentLength > maximumBytes)
            throw Reject(BackupDestinationRouteRejection.ObjectTooLarge,
                "Backup превышает допустимый adapter размер.");
        return adapter;
    }

    /// <summary>Возвращает metadata adapter только для allowlisted зарегистрированного kind.</summary>
    public IBackupDestinationAdapter GetRequired(string kind)
    {
        if (!AllowedKinds.Contains(kind) || !adapters.TryGetValue(kind, out var adapter))
            throw Reject(BackupDestinationRouteRejection.UnknownKind,
                $"Backup destination kind '{kind}' не разрешён.");
        return adapter;
    }

    private static bool RouteAllows(string allowedOperations, BackupDestinationOperation operation)
    {
        var expected = operation switch
        {
            BackupDestinationOperation.Put => "put",
            BackupDestinationOperation.Verify => "verify",
            BackupDestinationOperation.Materialize => "read",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return allowedOperations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expected, StringComparer.Ordinal);
    }

    private static BackupDestinationRouteException Reject(
        BackupDestinationRouteRejection rejection,
        string message) => new(rejection, message);
}

/// <summary>Консервативные capabilities существующей S3 PUT+HEAD реализации.</summary>
public sealed class S3BackupDestinationAdapter : IBackupDestinationAdapter
{
    /// <inheritdoc />
    public string Kind => "s3";
    /// <inheritdoc />
    public BackupDestinationCapabilities Capabilities { get; } = new(
        Put: new(true),
        Verify: new(true),
        Materialize: new(false),
        SupportsConditionalCreate: false,
        ProvidesNativeVersion: false,
        ProvidesNativeChecksum: false);
}
