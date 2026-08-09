using System.Text;
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
    /// <summary>Возвращает страницу только живых прокси, отсортированную по задержке.</summary>
    [HttpGet("proxies")]
    [OutputCache(PolicyName = "public-short")]
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
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = entities.Select(ProxyDto.From).ToList();
        var total = await query.CountAsync(cancellationToken);
        return Ok(new PagedResult<ProxyDto>(items, page, pageSize, total));
    }

    /// <summary>Экспортирует до 50 000 живых записей в json, xml, txt или csv.</summary>
    [HttpGet("export/{format}")]
    public async Task<IActionResult> Export(string format, [FromQuery] ProxyProtocol? protocol, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var freshAfter = DateTimeOffset.UtcNow.AddMinutes(-collectorOptions.Value.PublicFreshnessMinutes);
        var query = db.Proxies.AsNoTracking().Where(x =>
            x.Status == ProxyStatus.Alive && x.LastCheckedAt >= freshAfter);
        if (protocol.HasValue) query = query.Where(x => x.Protocol == protocol);
        var proxies = (await query.OrderBy(x => x.LatencyMs).Take(50_000).ToListAsync(cancellationToken)).Select(ProxyDto.From).ToList();
        var suffix = protocol?.ToString().ToLowerInvariant() ?? "all";
        return format.ToLowerInvariant() switch
        {
            "json" => File(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(proxies)), "application/json", $"proxies-{suffix}.json"),
            "txt" => File(Encoding.UTF8.GetBytes(string.Join('\n', proxies.Select(x => x.Url))), "text/plain", $"proxies-{suffix}.txt"),
            "csv" => File(Encoding.UTF8.GetBytes(ToCsv(proxies)), "text/csv", $"proxies-{suffix}.csv"),
            "xml" => File(Encoding.UTF8.GetBytes(ToXml(proxies)), "application/xml", $"proxies-{suffix}.xml"),
            _ => Problem("Поддерживаются форматы json, xml, txt и csv.", statusCode: 400)
        };
    }

    private static string ToCsv(IEnumerable<ProxyDto> proxies)
    {
        var rows = new List<string> { "protocol,host,port,latencyMs,successRate,lastCheckedAt" };
        rows.AddRange(proxies.Select(x => $"{x.Protocol},{x.Host},{x.Port},{x.LatencyMs},{x.SuccessRate},{x.LastCheckedAt:O}"));
        return string.Join('\n', rows);
    }

    private static string ToXml(IEnumerable<ProxyDto> proxies)
    {
        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false });
        writer.WriteStartElement("proxies");
        foreach (var proxy in proxies)
        {
            writer.WriteStartElement("proxy");
            writer.WriteElementString("protocol", proxy.Protocol.ToString().ToLowerInvariant());
            writer.WriteElementString("host", proxy.Host);
            writer.WriteElementString("port", proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString("latencyMs", proxy.LatencyMs?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString("successRate", proxy.SuccessRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.Flush();
        return builder.ToString();
    }
}

/// <summary>Стабильный публичный контракт без внутренних полей и ошибок.</summary>
public sealed record ProxyDto(string Host, int Port, ProxyProtocol Protocol, string Url, int? LatencyMs, decimal SuccessRate, string? ExitIp, DateTimeOffset? LastCheckedAt)
{
    public static ProxyDto From(ProxyEndpoint x) => new(x.Host, x.Port, x.Protocol,
        $"{x.Protocol.ToString().ToLowerInvariant()}://{(x.Host.Contains(':') ? $"[{x.Host}]" : x.Host)}:{x.Port}", x.LatencyMs, x.SuccessRate, x.ExitIp, x.LastCheckedAt);
}
