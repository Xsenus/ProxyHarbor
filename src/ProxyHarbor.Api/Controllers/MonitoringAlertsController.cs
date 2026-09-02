using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>
/// Принимает Alertmanager только по внутренней Docker-сети и переносит уведомление
/// в постоянную Telegram-очередь приложения. Bot token этому endpoint и
/// Alertmanager больше не нужны.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/v1/internal/monitoring/alerts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MonitoringAlertsController(
    IConfiguration configuration,
    ProxyHarborDbContext db,
    IBackupConfigurationStore backupConfigurations,
    TelegramDispatchService telegram) : ControllerBase
{
    private const int MaximumAlerts = 20;
    private const int MaximumMessageLength = 3_800;
    private const int IdempotencyWindowSeconds = 600;

    /// <summary>Ставит firing/resolved группу в durable очередь основного бота.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Receive([FromBody] AlertmanagerNotification request, CancellationToken token)
    {
        // Публичный reverse proxy сохраняет исходный Host. Внутренний Alertmanager
        // обращается непосредственно к service DNS name `api`, поэтому внешний
        // запрос скрывается как обычный 404 ещё до проверки отдельного секрета.
        if (!string.Equals(Request.Host.Host, "api", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var expected = configuration["Monitoring:AlertmanagerWebhookToken"];
        if (!IsStrongSecret(expected)) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!HasValidBearerToken(expected!)) return Unauthorized();

        if (!IsValid(request)) return BadRequest();

        var backup = await backupConfigurations.GetAsync(token);
        TelegramChat? recipient = null;
        if (backup.TelegramRecipientId.HasValue)
            recipient = await db.TelegramChats.AsNoTracking()
                .SingleOrDefaultAsync(chat => chat.Id == backup.TelegramRecipientId.Value, token);

        if (recipient is null && long.TryParse(backup.TelegramChatId,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId))
            recipient = await db.TelegramChats.AsNoTracking()
                .SingleOrDefaultAsync(chat => chat.ChatId == chatId, token);

        if (recipient is null || recipient.IsBlocked)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var now = DateTimeOffset.UtcNow;
        var key = CreateIdempotencyKey(request, now);
        var message = FormatMessage(request);
        await telegram.EnqueueTextAsync(recipient, message, key, direction: "bot", token: token);
        return Accepted(new { queued = true });
    }

    private bool HasValidBearerToken(string expected)
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) || values.Count != 1)
            return false;
        const string prefix = "Bearer ";
        var supplied = values[0];
        if (supplied is null || !supplied.StartsWith(prefix, StringComparison.Ordinal) ||
            supplied.Length != prefix.Length + expected.Length)
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied[prefix.Length..]);
        try { return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }

    private static bool IsStrongSecret(string? value) =>
        value is { Length: >= 32 and <= 256 } &&
        value.All(character => character is >= '!' and <= '~');

    private static bool IsValid(AlertmanagerNotification request) =>
        request.Version == "4" &&
        request.Receiver == "proxyharbor-api" &&
        request.Status is "firing" or "resolved" &&
        request.TruncatedAlerts >= 0 &&
        request.GroupKey is { Length: > 0 and <= 2_048 } &&
        request.Alerts is { Count: > 0 and <= MaximumAlerts } &&
        request.Alerts.All(alert =>
            alert.Status is "firing" or "resolved" &&
            alert.Fingerprint is { Length: <= 128 } &&
            alert.Labels is { Count: <= 64 } &&
            alert.Annotations is { Count: <= 64 });

    internal static string FormatMessage(AlertmanagerNotification request)
    {
        var firing = request.Status == "firing";
        var builder = new StringBuilder();
        builder.Append(firing ? "🔴 <b>ProxyHarbor: сработало предупреждение</b>" :
            "🟢 <b>ProxyHarbor: работа восстановлена</b>");

        foreach (var alert in request.Alerts.Take(MaximumAlerts))
        {
            var name = Value(alert.Labels, "alertname", "Без названия");
            var severity = Value(alert.Labels, "severity", "unknown");
            var summary = Value(alert.Annotations, "summary", string.Empty);
            var description = Value(alert.Annotations, "description", string.Empty);
            builder.Append("\n\n<b>").Append(Encode(name)).Append("</b> · ")
                .Append(Encode(severity));
            if (summary.Length > 0) builder.Append('\n').Append(Encode(summary));
            if (description.Length > 0 && !string.Equals(description, summary, StringComparison.Ordinal))
                builder.Append('\n').Append(Encode(description));
            if (TryFormatTimestamp(alert.StartsAt, out var startsAt))
                builder.Append("\nНачало: ").Append(startsAt);
            if (!firing && TryFormatTimestamp(alert.EndsAt, out var endsAt))
                builder.Append("\nЗавершено: ").Append(endsAt);

            if (builder.Length >= MaximumMessageLength) break;
        }

        if (request.TruncatedAlerts > 0)
            builder.Append("\n\nЕщё предупреждений: ").Append(request.TruncatedAlerts.ToString(CultureInfo.InvariantCulture));
        return TelegramDispatchService.Limit(builder.ToString(), MaximumMessageLength);
    }

    internal static string CreateIdempotencyKey(AlertmanagerNotification request, DateTimeOffset now)
    {
        var bucket = now.ToUnixTimeSeconds() / IdempotencyWindowSeconds;
        var fingerprints = string.Join(',', request.Alerts
            .Select(alert => alert.Fingerprint)
            .Order(StringComparer.Ordinal));
        var material = $"{request.Status}\n{request.GroupKey}\n{fingerprints}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"alertmanager:{bucket}:{hash}";
    }

    private static string Value(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? TelegramDispatchService.Limit(value.Trim(), 800)
            : fallback;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static bool TryFormatTimestamp(string? value, out string formatted)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            formatted = timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            return true;
        }
        formatted = string.Empty;
        return false;
    }
}

/// <summary>Bounded Alertmanager webhook v4 payload.</summary>
public sealed class AlertmanagerNotification
{
    /// <summary>Версия generic webhook schema; Alertmanager отправляет v4.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    /// <summary>Стабильный ключ группы маршрутизации.</summary>
    [JsonPropertyName("groupKey")] public string GroupKey { get; set; } = string.Empty;
    /// <summary>Общий firing/resolved статус уведомления.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    /// <summary>Имя receiver, сформировавшего webhook.</summary>
    [JsonPropertyName("receiver")] public string Receiver { get; set; } = string.Empty;
    /// <summary>Число записей сверх configured max_alerts.</summary>
    [JsonPropertyName("truncatedAlerts")] public int TruncatedAlerts { get; set; }
    /// <summary>Ограниченная группа alerts.</summary>
    [JsonPropertyName("alerts")] public List<AlertmanagerAlert> Alerts { get; set; } = [];
}

/// <summary>Одна firing/resolved запись из группы Alertmanager.</summary>
public sealed class AlertmanagerAlert
{
    /// <summary>Текущее состояние одной записи.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    /// <summary>Bounded Prometheus labels.</summary>
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Bounded человекочитаемые annotations.</summary>
    [JsonPropertyName("annotations")] public Dictionary<string, string> Annotations { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Время начала в RFC 3339.</summary>
    [JsonPropertyName("startsAt")] public string? StartsAt { get; set; }
    /// <summary>Время завершения в RFC 3339.</summary>
    [JsonPropertyName("endsAt")] public string? EndsAt { get; set; }
    /// <summary>Prometheus fingerprint для дедупликации.</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
}
