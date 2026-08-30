using System.IO.Compression;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Настройки локального справочника IP-адресов DB-IP Lite.</summary>
public sealed class GeoIpOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string Section = "GeoIp";
    /// <summary>Включает загрузку справочника и фоновое обогащение.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Абсолютный путь к рабочему MMDB-файлу.</summary>
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "geo-data", "dbip-country-lite.mmdb");
    /// <summary>Период проверки новой версии справочника.</summary>
    public int RefreshHours { get; set; } = 168;
    /// <summary>Максимальное число свежих Alive-прокси для одного прохода обогащения.</summary>
    public int BackfillBatchSize { get; set; } = 10_000;
}

/// <summary>
/// Потокобезопасно переиспользует memory-mapped MMDB reader. Один локальный lookup
/// не выполняет сетевых запросов и пригоден для массового обогащения каталога.
/// </summary>
public sealed class ProxyCountryResolver(IOptions<GeoIpOptions> options) : IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new();
    private DatabaseReader? _reader;

    /// <summary>Заменяет открытый справочник новой атомарно загруженной версией.</summary>
    public bool Reload()
    {
        var path = options.Value.DatabasePath;
        if (!File.Exists(path)) return false;
        var replacement = new DatabaseReader(path, FileAccessMode.MemoryMapped);
        _gate.EnterWriteLock();
        try
        {
            var previous = _reader;
            _reader = replacement;
            previous?.Dispose();
            return true;
        }
        finally { _gate.ExitWriteLock(); }
    }

    /// <summary>Возвращает ISO 3166-1 alpha-2 либо null для неизвестного/некорректного IP.</summary>
    public string? Resolve(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)) return null;
        _gate.EnterReadLock();
        try
        {
            if (_reader is null || !_reader.TryCountry(address, out var response)) return null;
            var code = response.Country.IsoCode?.ToUpperInvariant();
            return code is { Length: 2 } ? code : null;
        }
        finally { _gate.ExitReadLock(); }
    }

    /// <summary>Освобождает memory-mapped справочник.</summary>
    public void Dispose()
    {
        _gate.EnterWriteLock();
        try { _reader?.Dispose(); _reader = null; }
        finally { _gate.ExitWriteLock(); _gate.Dispose(); }
    }
}

/// <summary>
/// Загружает ежемесячный DB-IP Country Lite и дозаполняет страны у свежих Alive-прокси.
/// Сбой внешнего справочника не влияет на проверку и публикацию самих прокси.
/// </summary>
public sealed class ProxyCountryWorker(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    ProxyCountryResolver resolver,
    IOptions<GeoIpOptions> options,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<ProxyCountryWorker> logger) : BackgroundService
{
    private const long MaxCompressedBytes = 64L * 1024 * 1024;
    private const long MaxDatabaseBytes = 128L * 1024 * 1024;
    private static readonly Action<ILogger, string, Exception?> DatabaseReady =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1601, "GeoIpDatabaseReady"),
            "Локальный справочник стран прокси готов: {DatabasePath}");
    private static readonly Action<ILogger, int, Exception?> BackfillCompleted =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1602, "GeoIpBackfillCompleted"),
            "Страна заполнена для {ProxyCount} proxy/VPN endpoint");
    private static readonly Action<ILogger, Exception?> RefreshFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1603, "GeoIpRefreshFailed"),
            "Не удалось обновить локальный справочник стран; работа прокси продолжается без геофильтра для неизвестных адресов");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !collectorOptions.Value.BackgroundWorkersEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureDatabaseAsync(stoppingToken);
                if (resolver.Reload())
                {
                    DatabaseReady(logger, options.Value.DatabasePath, null);
                    var updated = await BackfillAsync(stoppingToken);
                    if (updated > 0) BackfillCompleted(logger, updated, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { RefreshFailed(logger, exception); }

            // Новые результаты проверки получают страну максимум через пять минут;
            // дорогое обновление самого MMDB отдельно ограничено RefreshHours.
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken token)
    {
        var path = options.Value.DatabasePath;
        var existing = new FileInfo(path);
        if (existing.Exists && DateTimeOffset.UtcNow - existing.LastWriteTimeUtc <
            TimeSpan.FromHours(options.Value.RefreshHours)) return;

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("GeoIp DatabasePath не содержит каталога.");
        Directory.CreateDirectory(directory);
        var compressedPath = $"{path}.{Guid.NewGuid():N}.gz";
        var databasePath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var client = httpClientFactory.CreateClient("geoip");
            HttpResponseMessage? response = null;
            for (var monthOffset = 0; monthOffset <= 1; monthOffset++)
            {
                var month = DateTimeOffset.UtcNow.AddMonths(-monthOffset);
                var url = $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz";
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.IsSuccessStatusCode) break;
                response.Dispose();
                response = null;
            }
            using (response)
            {
                if (response is null) throw new HttpRequestException("DB-IP не отдал текущий или предыдущий ежемесячный справочник.");
                if (response.Content.Headers.ContentLength > MaxCompressedBytes)
                    throw new InvalidDataException("Сжатый DB-IP справочник превышает допустимый размер.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var compressed = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyBoundedAsync(input, compressed, MaxCompressedBytes, token);
            }

            await using (var compressed = File.OpenRead(compressedPath))
            await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            await using (var output = new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(gzip, output, MaxDatabaseBytes, token);

            // Проверяем формат до атомарной замены рабочего файла.
            using (var verification = new DatabaseReader(databasePath, FileAccessMode.MemoryMapped))
                _ = verification.Metadata.DatabaseType;
            File.Move(databasePath, path, overwrite: true);
        }
        finally
        {
            File.Delete(compressedPath);
            File.Delete(databasePath);
        }
    }

    private async Task<int> BackfillAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        // Перепроверяем и уже заполненные строки: выходной IP прокси может измениться,
        // поэтому сохранённая страна должна всегда соответствовать последней проверке.
        var candidates = await db.Proxies
            .AsNoTracking()
            .Where(proxy => proxy.Status == ProxyStatus.Alive && proxy.ExitIp != null)
            .OrderByDescending(proxy => proxy.LastCheckedAt)
            .Take(options.Value.BackfillBatchSize)
            .Select(proxy => new { proxy.Id, proxy.ExitIp, proxy.CountryCode })
            .ToListAsync(token);
        var proxyUpdates = BuildCountryUpdates(
            candidates.Select(proxy => new CountryCandidate(proxy.Id, proxy.ExitIp!, proxy.CountryCode)),
            resolver.Resolve);

        // VPN использует ту же локальную GeoIP-базу. Никаких внешних запросов по
        // каждому адресу нет; DNS-имена останутся без страны до появления IP.
        var vpnCandidates = await db.VpnEndpoints
            .AsNoTracking()
            .Where(endpoint => endpoint.Status == VpnEndpointStatus.Reachable)
            .OrderByDescending(endpoint => endpoint.LastCheckedAt)
            .Take(options.Value.BackfillBatchSize)
            .Select(endpoint => new { endpoint.Id, endpoint.Host, endpoint.CountryCode })
            .ToListAsync(token);
        var vpnUpdates = BuildCountryUpdates(
            vpnCandidates.Select(endpoint => new CountryCandidate(endpoint.Id, endpoint.Host, endpoint.CountryCode)),
            resolver.Resolve);

        // Proxy and VPN tables use separate short transactions. Validation can continue
        // concurrently; rows already leased by another writer are skipped and retried by
        // the next five-minute pass instead of forming a cross-batch deadlock.
        var updated = await PersistCountryUpdatesAsync(dbFactory, CountryCatalog.Proxy, proxyUpdates, token);
        updated += await PersistCountryUpdatesAsync(dbFactory, CountryCatalog.Vpn, vpnUpdates, token);
        return updated;
    }

    /// <summary>Нормализует GeoIP-результаты и отбрасывает неизвестные либо неизменившиеся страны.</summary>
    internal static CountryCodeUpdate[] BuildCountryUpdates(
        IEnumerable<CountryCandidate> candidates,
        Func<string, string?> resolve)
    {
        var updates = new List<CountryCodeUpdate>();
        foreach (var candidate in candidates)
        {
            var country = resolve(candidate.Address)?.ToUpperInvariant();
            if (country is not { Length: 2 } || string.Equals(candidate.CurrentCountryCode, country, StringComparison.Ordinal))
                continue;
            updates.Add(new CountryCodeUpdate(candidate.Id, country));
        }
        return updates.ToArray();
    }

    /// <summary>
    /// Copies a bounded country batch into PostgreSQL and updates only rows that can be
    /// locked immediately in stable ID order. A skipped row is deliberately not an error:
    /// the periodic backfill will resolve it after the active validator releases its lock.
    /// </summary>
    internal static async Task<int> PersistCountryUpdatesAsync(
        IDbContextFactory<ProxyHarborDbContext> factory,
        CountryCatalog catalog,
        CountryCodeUpdate[] updates,
        CancellationToken token = default)
    {
        if (updates.Length == 0) return 0;
        if (updates.Any(update => update.CountryCode is null || update.CountryCode.Length != 2 ||
            update.CountryCode.Any(character => character is < 'A' or > 'Z')))
            throw new ArgumentException("CountryCode должен быть ISO alpha-2 в верхнем регистре.", nameof(updates));
        if (updates.Select(update => update.Id).Distinct().Count() != updates.Length)
            throw new ArgumentException("Country update не должен содержать повторяющиеся ID.", nameof(updates));

        await using var strategyDb = await factory.CreateDbContextAsync(token);
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var writeDb = await factory.CreateDbContextAsync(token);
            var connection = (NpgsqlConnection)writeDb.Database.GetDbConnection();
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var create = new NpgsqlCommand("""
                CREATE TEMP TABLE geoip_country_update (
                    id uuid PRIMARY KEY,
                    country_code varchar(2) NOT NULL
                ) ON COMMIT DROP
                """, connection, transaction))
                await create.ExecuteNonQueryAsync(token);

            await using (var writer = await connection.BeginBinaryImportAsync("""
                COPY geoip_country_update (id, country_code) FROM STDIN (FORMAT BINARY)
                """, token))
            {
                foreach (var update in updates.OrderBy(update => update.Id))
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(update.Id, NpgsqlDbType.Uuid, token);
                    await writer.WriteAsync(update.CountryCode, NpgsqlDbType.Varchar, token);
                }
                await writer.CompleteAsync(token);
            }

            var table = CountryTableName(catalog);
            await using var merge = new NpgsqlCommand($$"""
                WITH locked AS MATERIALIZED (
                    SELECT target."Id"
                    FROM "{{table}}" target
                    JOIN geoip_country_update incoming ON incoming.id = target."Id"
                    ORDER BY target."Id"
                    FOR UPDATE OF target SKIP LOCKED
                )
                UPDATE "{{table}}" target
                SET "CountryCode" = incoming.country_code
                FROM geoip_country_update incoming
                JOIN locked ON locked."Id" = incoming.id
                WHERE target."Id" = incoming.id
                  AND target."CountryCode" IS DISTINCT FROM incoming.country_code
                """, connection, transaction);
            var persisted = await merge.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            return persisted;
        });
    }

    /// <summary>Возвращает только одно из двух заранее разрешённых имён таблиц для SQL merge.</summary>
    internal static string CountryTableName(CountryCatalog catalog) => catalog switch
    {
        CountryCatalog.Proxy => "Proxies",
        CountryCatalog.Vpn => "VpnEndpoints",
        _ => throw new ArgumentOutOfRangeException(nameof(catalog))
    };

    // Открыт внутри сборки для детерминированной проверки ограничения размера без сетевых запросов.
    internal static async Task CopyBoundedAsync(Stream input, Stream output, long maximumBytes, CancellationToken token)
    {
        var buffer = new byte[81_920];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            written += read;
            if (written > maximumBytes) throw new InvalidDataException("GeoIp файл превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}

/// <summary>Каталог, которому принадлежит безопасное GeoIP-обновление.</summary>
internal enum CountryCatalog
{
    /// <summary>Публичные proxy endpoint.</summary>
    Proxy,
    /// <summary>Публичные VPN endpoint.</summary>
    Vpn
}

/// <summary>Минимальная строка пакетного обновления страны.</summary>
internal readonly record struct CountryCodeUpdate(Guid Id, string? CountryCode);

/// <summary>Минимальная входная строка для локального GeoIP lookup.</summary>
internal readonly record struct CountryCandidate(Guid Id, string Address, string? CurrentCountryCode);
