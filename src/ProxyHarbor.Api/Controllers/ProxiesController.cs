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
    private const int ExportLimit = 50_000;
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
        [FromQuery] ProxyProtocol? protocol,
        [FromQuery] int? maxLatencyMs,
        [FromQuery] decimal? minSuccessRate,
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
        var query = db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter);
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol);
        if (maxLatencyMs.HasValue) query = query.Where(x => x.LatencyMs <= maxLatencyMs);
        if (minSuccessRate.HasValue)
        {
            var threshold = Math.Clamp(minSuccessRate.Value, 0, 100);
            query = query.Where(x => x.SuccessfulChecks + x.FailedChecks > 0 &&
                100m * x.SuccessfulChecks >= threshold * (x.SuccessfulChecks + x.FailedChecks));
        }
        var entities = await query.OrderBy(x => x.LatencyMs).ThenByDescending(x => x.SuccessfulChecks)
            .Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
        var items = entities.Select(ProxyDto.From).ToList();
        var total = await query.CountAsync(cancellationToken);
        return Ok(new PagedResult<ProxyDto>(items, page, pageSize, total));
    }

    /// <summary>Потоково экспортирует до 50 000 живых записей в json, xml, txt или csv.</summary>
    [HttpGet("export/{format}")]
    [EnableRateLimiting("export")]
    public async Task<IActionResult> Export(string format, [FromQuery] ProxyProtocol? protocol, CancellationToken cancellationToken)
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
            var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
            var query = db.Proxies.AsNoTracking().Where(x =>
                x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter);
            if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol);
            var proxies = query.OrderBy(x => x.LatencyMs).ThenBy(x => x.Id).Take(ExportLimit)
                .AsAsyncEnumerable().Select(ProxyDto.From);
            var suffix = protocol?.ToString().ToLowerInvariant() ?? "all";
            Response.ContentType = contentType;
            Response.Headers.ContentDisposition = $"attachment; filename=\"proxies-{suffix}.{normalizedFormat}\"";
            Response.Headers.CacheControl = "no-store";

            switch (normalizedFormat)
            {
                case "json": await WriteJsonAsync(Response.Body, proxies, cancellationToken); break;
                case "txt": await WriteTextAsync(Response.Body, proxies, cancellationToken); break;
                case "csv": await WriteCsvAsync(Response.Body, proxies, cancellationToken); break;
                case "xml": await WriteXmlAsync(Response.Body, proxies, cancellationToken); break;
            }
            return new EmptyResult();
        }
        finally { ExportConcurrencyGate.Release(); }
    }

    private static async Task WriteJsonAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        // SerializeAsync умеет потоково перечислять IAsyncEnumerable и не выполняет
        // запрещённый Kestrel synchronous flush при включённом Brotli/Gzip.
        await JsonSerializer.SerializeAsync(output, proxies, ExportJsonOptions, token);
    }

    private static async Task WriteTextAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        await using var writer = new StreamWriter(output, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await foreach (var proxy in proxies.WithCancellation(token))
            await writer.WriteLineAsync(proxy.Url.AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static async Task WriteCsvAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        await using var writer = new StreamWriter(output, Utf8NoBom, 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("protocol,host,port,latencyMs,successRate,lastCheckedAt".AsMemory(), token);
        await foreach (var proxy in proxies.WithCancellation(token))
        {
            var row = FormattableString.Invariant(
                $"{proxy.Protocol},{proxy.Host},{proxy.Port},{proxy.LatencyMs},{proxy.SuccessRate},{proxy.LastCheckedAt:O}");
            await writer.WriteLineAsync(row.AsMemory(), token);
        }
        await writer.FlushAsync(token);
    }

    private static async Task WriteXmlAsync(Stream output, IAsyncEnumerable<ProxyDto> proxies, CancellationToken token)
    {
        using var writer = XmlWriter.Create(output, new XmlWriterSettings
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
            await writer.WriteElementStringAsync(null, "protocol", null, proxy.Protocol.ToString().ToLowerInvariant());
            await writer.WriteElementStringAsync(null, "host", null, proxy.Host);
            await writer.WriteElementStringAsync(null, "port", null, proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "latencyMs", null,
                proxy.LatencyMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            await writer.WriteElementStringAsync(null, "successRate", null, proxy.SuccessRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await writer.WriteEndElementAsync();
        }
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
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
