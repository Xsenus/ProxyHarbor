using System.IO.Compression;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            .Where(proxy => proxy.Status == ProxyStatus.Alive && proxy.ExitIp != null)
            .OrderByDescending(proxy => proxy.LastCheckedAt)
            .Take(options.Value.BackfillBatchSize)
            .ToListAsync(token);
        var updated = 0;
        foreach (var proxy in candidates)
        {
            var country = resolver.Resolve(proxy.ExitIp);
            if (country is null || string.Equals(proxy.CountryCode, country, StringComparison.Ordinal)) continue;
            proxy.CountryCode = country;
            updated++;
        }

        // VPN использует ту же локальную GeoIP-базу. Никаких внешних запросов по
        // каждому адресу нет; DNS-имена останутся без страны до появления IP.
        var vpnCandidates = await db.VpnEndpoints
            .Where(endpoint => endpoint.Status == VpnEndpointStatus.Reachable)
            .OrderByDescending(endpoint => endpoint.LastCheckedAt)
            .Take(options.Value.BackfillBatchSize)
            .ToListAsync(token);
        foreach (var endpoint in vpnCandidates)
        {
            var country = resolver.Resolve(endpoint.Host);
            if (country is null || string.Equals(endpoint.CountryCode, country, StringComparison.Ordinal)) continue;
            endpoint.CountryCode = country;
            updated++;
        }
        if (updated > 0) await db.SaveChangesAsync(token);
        return updated;
    }

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
