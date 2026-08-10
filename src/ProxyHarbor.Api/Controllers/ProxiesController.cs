using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Публичный каталог проверенных прокси и экспорты.</summary>
[ApiController, Route("api/v1"), EnableRateLimiting("public")]
public sealed class ProxiesController(
    IDbContextFactory<ProxyHarborDbContext> dbFactory,
    IOptions<CollectorOptions> collectorOptions) : ControllerBase
{
    private const int MaxExportPageSize = 50_000;
    private const int MaxLegacyOffset = 5_000_000;
    private const int ConcurrentExportLimit = 2;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly SemaphoreSlim ExportConcurrencyGate = new(ConcurrentExportLimit, ConcurrentExportLimit);
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Возвращает страницу только живых прокси, отсортированную по задержке.</summary>
    [HttpGet("proxies")]
    [OutputCache(PolicyName = "public-list")]
    public async Task<ActionResult<PagedResult<ProxyDto>>> Get(
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var requestedOffset = (long)(page - 1) * pageSize;
        if (requestedOffset > 5_000_000)
            return BadRequest(new ProblemDetails
            {
                Title = "Слишком глубокая страница",
                Detail = "Используйте больший pageSize или экспорт; максимальное смещение — 5 000 000 записей.",
                Status = 400
            });
        var skip = (int)requestedOffset;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter), protocol, maxLatencyMs, minSuccessRate);
        var entities = await OrderForPublication(query)
            .Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
        var items = entities.Select(ProxyDto.From).ToList();
        var total = await query.CountAsync(cancellationToken);
        return Ok(new PagedResult<ProxyDto>(items, page, pageSize, total));
    }

    /// <summary>
    /// Возвращает keyset-страницу без растущего OFFSET и дорогостоящего точного COUNT.
    /// Следующий запрос передаёт непрозрачный NextCursor в параметре after.
    /// </summary>
    [HttpGet("proxies/seek")]
    [OutputCache(PolicyName = PublicOutputCachePolicies.SeekFirstPage)]
    public async Task<ActionResult<CursorPagedResult<ProxyDto>>> Seek(
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        [FromQuery, StringLength(PublicationCursor.EncodedLength)] string? after,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var fingerprint = PublicationCursor.FilterFingerprint(protocol, maxLatencyMs, minSuccessRate);
        PublicationPosition? position = null;
        if (after is not null)
        {
            if (!PublicationCursor.TryDecode(after, fingerprint, out var decoded))
                return InvalidCursor();
            position = decoded;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter && x.LatencyMs != null),
            protocol, maxLatencyMs, minSuccessRate);
        if (position.HasValue) query = ApplyAfter(query, position.Value);
        var entities = await OrderForPublication(query).Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = entities.Count > pageSize;
        if (hasMore) entities.RemoveAt(pageSize);
        var nextCursor = hasMore ? EncodePosition(entities[^1], fingerprint) : null;
        return Ok(new CursorPagedResult<ProxyDto>(
            entities.Select(ProxyDto.From).ToList(), pageSize, hasMore, nextCursor));
    }

    /// <summary>Потоково экспортирует страницу живых записей в json, xml, txt или csv.</summary>
    [HttpGet("export/{format}")]
    [EnableRateLimiting("export")]
    public async Task<IActionResult> Export(
        string format,
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, MaxExportPageSize)] int limit = MaxExportPageSize,
        [FromQuery, Range(0, MaxLegacyOffset)] int offset = 0)
    {
        if (offset > MaxLegacyOffset)
            return BadRequest(new ProblemDetails
            {
                Title = "Слишком глубокий экспорт",
                Detail = "Используйте /export/{format}/seek; максимальное legacy-смещение — 5 000 000 записей.",
                Status = StatusCodes.Status400BadRequest
            });
        return await ExportCore(
            format, protocol, maxLatencyMs, minSuccessRate, limit, offset, after: null, cancellationToken);
    }

    /// <summary>Потоково экспортирует keyset-страницу с постоянной стоимостью продолжения.</summary>
    [HttpGet("export/{format}/seek")]
    [EnableRateLimiting("export")]
    public async Task<IActionResult> ExportSeek(
        string format,
        [FromQuery, EnumDataType(typeof(ProxyProtocol))] ProxyProtocol? protocol,
        [FromQuery, Range(1, int.MaxValue)] int? maxLatencyMs,
        [FromQuery, Range(typeof(decimal), "0", "100")] decimal? minSuccessRate,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, MaxExportPageSize)] int limit = MaxExportPageSize,
        [FromQuery, StringLength(PublicationCursor.EncodedLength)] string? after = null)
        => await ExportCore(
            format, protocol, maxLatencyMs, minSuccessRate, limit, offset: null, after, cancellationToken);

    private async Task<IActionResult> ExportCore(
        string format,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate,
        int limit,
        int? offset,
        string? after,
        CancellationToken cancellationToken)
    {
        var normalizedFormat = format.ToLowerInvariant();
        var contentType = normalizedFormat switch
        {
            "json" => "application/json; charset=utf-8",
            "txt" => "text/plain; charset=utf-8",
            "csv" => "text/csv; charset=utf-8",
            "xml" => "application/xml; charset=utf-8",
            _ => null
        };
        if (contentType is null)
            return Problem("Поддерживаются форматы json, xml, txt и csv.", statusCode: 400);
        var seekMode = !offset.HasValue;
        var fingerprint = PublicationCursor.FilterFingerprint(protocol, maxLatencyMs, minSuccessRate);
        PublicationPosition? position = null;
        if (seekMode && after is not null)
        {
            if (!PublicationCursor.TryDecode(after, fingerprint, out var decoded))
                return InvalidCursor();
            position = decoded;
        }
        if (!await ExportConcurrencyGate.WaitAsync(0, cancellationToken))
        {
            Response.Headers.RetryAfter = "1";
            return Problem(
                "Сервис уже формирует максимально допустимое число экспортов; повторите запрос через секунду.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // Boundary headers и streaming body обязаны видеть один набор строк. Иначе
            // validation update между двумя SQL-командами способен выдать cursor от одной
            // страницы, а тело — от другой. InMemory unit provider транзакций не имеет.
            await using var snapshot = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                : null;
            var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
            var query = ApplyFilters(db.Proxies.AsNoTracking().Where(x =>
                x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter), protocol, maxLatencyMs, minSuccessRate);
            if (seekMode)
            {
                query = query.Where(x => x.LatencyMs != null);
                if (position.HasValue) query = ApplyAfter(query, position.Value);
            }
            var ordered = OrderForPublication(query);
            var legacyOffset = offset ?? 0;
            var nextOffset = (long)legacyOffset + limit;
            // Предварительный index-only boundary-запрос не материализует body и
            // позволяет выставить continuation headers до первой потоковой записи.
            var pageQuery = seekMode ? ordered : ordered.Skip(legacyOffset);
            bool hasMore;
            string? nextCursor = null;
            if (seekMode)
            {
                // Последняя включённая строка и один look-ahead дают hasMore и cursor
                // одним round-trip вместо двух одинаковых проходов по индексу.
                var boundary = await pageQuery.Skip(limit - 1).Take(2)
                    .Select(x => new PublicationPosition(x.LatencyMs!.Value, x.SuccessfulChecks, x.Id))
                    .ToListAsync(cancellationToken);
                hasMore = boundary.Count == 2;
                if (hasMore) nextCursor = PublicationCursor.Encode(boundary[0], fingerprint);
            }
            else
            {
                hasMore = await ordered.Skip((int)nextOffset).AnyAsync(cancellationToken);
            }
            var proxies = pageQuery.Take(limit)
                .AsAsyncEnumerable().Select(ProxyDto.From);
            var suffix = protocol?.ToString().ToLowerInvariant() ?? "all";
            Response.ContentType = contentType;
            var pageSuffix = seekMode ? "-seek" : legacyOffset == 0 ? string.Empty : $"-offset-{legacyOffset}";
            Response.Headers.ContentDisposition =
                $"attachment; filename=\"proxies-{suffix}{pageSuffix}.{normalizedFormat}\"";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Export-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Export-Truncated"] = hasMore ? "true" : "false";
            if (seekMode)
            {
                Response.Headers["X-Export-Cursor"] = after ?? "start";
                if (nextCursor is not null) Response.Headers["X-Next-Cursor"] = nextCursor;
            }
            else
            {
                Response.Headers["X-Export-Offset"] = legacyOffset.ToString(CultureInfo.InvariantCulture);
            }
            if (!seekMode && hasMore)
                Response.Headers["X-Next-Offset"] = nextOffset.ToString(CultureInfo.InvariantCulture);

            switch (normalizedFormat)
            {
                case "json": await WriteJsonAsync(Response.Body, proxies, cancellationToken); break;
                case "txt": await WriteTextAsync(Response.Body, proxies, cancellationToken); break;
                case "csv": await WriteCsvAsync(Response.Body, proxies, cancellationToken); break;
                case "xml": await WriteXmlAsync(Response.Body, proxies, cancellationToken); break;
            }
            if (snapshot is not null) await snapshot.CommitAsync(cancellationToken);
            return new EmptyResult();
        }
        finally { ExportConcurrencyGate.Release(); }
    }

    private BadRequestObjectResult InvalidCursor() => BadRequest(new ProblemDetails
    {
        Title = "Некорректный cursor",
        Detail = "Cursor повреждён, устарел либо был создан для другого набора фильтров.",
        Status = StatusCodes.Status400BadRequest
    });

    private static async Task WriteJsonAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        // SerializeAsync умеет потоково перечислять IAsyncEnumerable и не выполняет
        // запрещённый Kestrel synchronous flush при включённом Brotli/Gzip.
        await JsonSerializer.SerializeAsync(output, proxies, ExportJsonOptions, token);
    }

    private static async Task WriteTextAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        using var cancellationBoundOutput = new CancellationBoundOutputStream(output, token);
        await using var writer = new StreamWriter(cancellationBoundOutput, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await foreach (var proxy in proxies.WithCancellation(token))
            await writer.WriteLineAsync(proxy.Url.AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static async Task WriteCsvAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        using var cancellationBoundOutput = new CancellationBoundOutputStream(output, token);
        await using var writer = new StreamWriter(cancellationBoundOutput, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("protocol,host,port,latencyMs,successRate,lastCheckedAt,url,exitIp".AsMemory(), token);
        await foreach (var proxy in proxies.WithCancellation(token))
        {
            var row = string.Join(',', new[]
            {
                CsvField(proxy.Protocol.ToString()),
                CsvField(proxy.Host),
                proxy.Port.ToString(CultureInfo.InvariantCulture),
                proxy.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                proxy.SuccessRate.ToString(CultureInfo.InvariantCulture),
                CsvField(proxy.LastCheckedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                CsvField(proxy.Url),
                CsvField(proxy.ExitIp ?? string.Empty)
            });
            await writer.WriteLineAsync(row.AsMemory(), token);
        }
        await writer.FlushAsync(token);
    }

    private static async Task WriteXmlAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        // XmlWriter.Dispose выполняет синхронный Flush даже после FlushAsync. Kestrel и
        // compression streams запрещают его, поэтому обёртка поглощает только финальный
        // избыточный Flush, сохраняя запрет на любые синхронные записи.
        // XmlWriter async API не принимает CancellationToken для отдельных записей.
        // Адаптер поэтому навязывает request token каждому обращению к Response.Body.
        using var asyncOutput = new CancellationBoundOutputStream(output, token);
        using var writer = XmlWriter.Create(asyncOutput, new XmlWriterSettings
        {
            Async = true,
            Encoding = Utf8NoBom,
            Indent = false,
            CloseOutput = false
        });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "proxies", null);
        await foreach (var proxy in proxies.WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteStartElementAsync(null, "proxy", null);
            await writer.WriteElementStringAsync(null, "protocol", null, proxy.Protocol.ToString());
            await writer.WriteElementStringAsync(null, "host", null, proxy.Host);
            await writer.WriteElementStringAsync(null, "port", null, proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "latencyMs", null,
                proxy.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            await writer.WriteElementStringAsync(null, "successRate", null, proxy.SuccessRate.ToString(CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "lastCheckedAt", null,
                proxy.LastCheckedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            await writer.WriteElementStringAsync(null, "url", null, proxy.Url);
            await writer.WriteElementStringAsync(null, "exitIp", null, proxy.ExitIp ?? string.Empty);
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    /// <summary>Применяет одинаковые ограничения к постраничной и потоковой публичной выдаче.</summary>
    private static IQueryable<ProxyEndpoint> ApplyFilters(
        IQueryable<ProxyEndpoint> query,
        ProxyProtocol? protocol,
        int? maxLatencyMs,
        decimal? minSuccessRate)
    {
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol);
        if (maxLatencyMs.HasValue) query = query.Where(x => x.LatencyMs <= maxLatencyMs);
        if (minSuccessRate.HasValue)
        {
            var threshold = minSuccessRate.Value;
            query = query.Where(x => x.SuccessfulChecks + x.FailedChecks > 0 &&
                100m * x.SuccessfulChecks >= threshold * (x.SuccessfulChecks + x.FailedChecks));
        }
        return query;
    }

    /// <summary>
    /// Задаёт единый полный порядок для списка и каждого export-формата.
    /// UUID устраняет неоднозначность между строками с одинаковой latency/statistics,
    /// поэтому последовательные offset-страницы не повторяют и не пропускают записи.
    /// </summary>
    private static IOrderedQueryable<ProxyEndpoint> OrderForPublication(IQueryable<ProxyEndpoint> query) =>
        query.OrderBy(x => x.LatencyMs)
            .ThenByDescending(x => x.SuccessfulChecks)
            .ThenBy(x => x.Id);

    /// <summary>Формирует sargable lexicographic predicate под partial public-order index.</summary>
    internal static IQueryable<ProxyEndpoint> ApplyAfter(
        IQueryable<ProxyEndpoint> query,
        PublicationPosition position) =>
        query.Where(x => x.LatencyMs > position.LatencyMs ||
            x.LatencyMs == position.LatencyMs &&
            (x.SuccessfulChecks < position.SuccessfulChecks ||
                x.SuccessfulChecks == position.SuccessfulChecks && x.Id.CompareTo(position.Id) > 0));

    private static string EncodePosition(ProxyEndpoint endpoint, ulong fingerprint) =>
        PublicationCursor.Encode(
            new PublicationPosition(endpoint.LatencyMs!.Value, endpoint.SuccessfulChecks, endpoint.Id), fingerprint);

    /// <summary>Кавычит строковое CSV-поле и нейтрализует spreadsheet formula injection.</summary>
    private static string CsvField(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = $"'{value}";
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Навязывает request token каждому async write/flush, включая финальный flush из
    /// DisposeAsync writer'а, чтобы отключившийся клиент немедленно освобождал export slot.
    /// </summary>
    private sealed class CancellationBoundOutputStream(Stream inner, CancellationToken requestToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Перед Dispose всегда выполняется XmlWriter.FlushAsync; повторный sync flush не нужен.
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.FlushAsync(requestToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous XML response writes are forbidden.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(buffer, offset, count, requestToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            requestToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(buffer, requestToken);
        }

        protected override void Dispose(bool disposing)
        {
            // Жизненным циклом исходного Response.Body владеет ASP.NET Core.
            base.Dispose(disposing);
        }
    }
}

/// <summary>Стабильный публичный контракт без внутренних полей и ошибок.</summary>
public sealed record ProxyDto(string Host, int Port, ProxyProtocol Protocol, string Url, int? LatencyMs, decimal SuccessRate, string? ExitIp, DateTimeOffset? LastCheckedAt)
{
    public static ProxyDto From(ProxyEndpoint x)
    {
        // Категория HTTPS в публичных free-list означает HTTP CONNECT, а не TLS до proxy endpoint.
        var transportScheme = x.Protocol is ProxyProtocol.Http or ProxyProtocol.Https
            ? "http"
            : x.Protocol.ToString().ToLowerInvariant();
        var host = x.Host.Contains(':') ? $"[{x.Host}]" : x.Host;
        return new ProxyDto(x.Host, x.Port, x.Protocol, $"{transportScheme}://{host}:{x.Port}",
            x.LatencyMs, x.SuccessRate, x.ExitIp, x.LastCheckedAt);
    }
}

/// <summary>Bounded keyset-страница без линейного offset и полного count.</summary>
public sealed record CursorPagedResult<T>(IReadOnlyList<T> Items, int PageSize, bool HasMore, string? NextCursor);
