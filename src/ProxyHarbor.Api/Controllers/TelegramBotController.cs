using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api.Controllers;

/// <summary>Защищённая входная точка Telegram webhook.</summary>
[ApiController, Route("api/v1/telegram"), AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TelegramWebhookController(
    ITelegramBotConfigurationStore configurations,
    TelegramUpdateProcessor processor) : ControllerBase
{
    /// <summary>Принимает update только для настроенного username и с secret header.</summary>
    [HttpPost("webhook/{botUsername}"), IgnoreAntiforgeryToken, EnableRateLimiting("telegram-webhook")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Webhook(string botUsername, [FromBody] JsonElement update, CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        if (!options.Ready || options.UpdateMode != TelegramUpdateModes.Webhook ||
            !string.Equals(options.BotUsername, botUsername, StringComparison.OrdinalIgnoreCase)) return NotFound();
        var actual = Request.Headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault() ?? string.Empty;
        if (!FixedEquals(options.WebhookSecret, actual)) return Unauthorized();
        await processor.ProcessAsync(update, TelegramUpdateModes.Webhook, token);
        return Ok();
    }

    private static bool FixedEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>Настройка, статистика и CRM торгового Telegram-бота.</summary>
[ApiController, Route("api/v1/admin/telegram"), EnableRateLimiting("admin")]
[Authorize(Roles = UserRoles.Administrator)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminTelegramController(
    ProxyHarborDbContext db,
    ITelegramBotConfigurationStore configurations,
    IPaymentConfigurationStore payments,
    TelegramBotApiClient api,
    TelegramDispatchService queue) : ControllerBase
{
    /// <summary>Возвращает безопасные настройки и операционную статистику без token.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        var catalog = await payments.GetAsync(token);
        var effectiveProductStars = catalog.Products
            .Where(x => TelegramStarsPricing.TryResolve(options, x.Key, x.Value, out _))
            .ToDictionary(x => x.Key, x =>
            {
                _ = TelegramStarsPricing.TryResolve(options, x.Key, x.Value, out var stars);
                return stars;
            }, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var stats = new
        {
            users = await db.TelegramChats.CountAsync(token),
            activeUsers30d = await db.TelegramChats.CountAsync(x => x.LastInteractionAt >= now.AddDays(-30), token),
            notifications = await db.TelegramChats.CountAsync(x => x.NotificationsEnabled && !x.IsBlocked, token),
            blocked = await db.TelegramChats.CountAsync(x => x.IsBlocked, token),
            paidOrders = await db.PaymentOrders.CountAsync(x => x.Provider == "telegram_stars" && x.Status == PaymentStatuses.Paid, token),
            starsRevenue = await db.PaymentOrders.Where(x => x.Provider == "telegram_stars" && x.Status == PaymentStatuses.Paid)
                .SumAsync(x => (long?)x.AmountMinor, token) ?? 0,
            queued = await db.TelegramOutboundMessages.CountAsync(x => x.Status == TelegramOutboundStatuses.Pending || x.Status == TelegramOutboundStatuses.Processing, token),
            failed = await db.TelegramOutboundMessages.CountAsync(x => x.Status == TelegramOutboundStatuses.Failed, token)
        };
        return Ok(new
        {
            options.Enabled,
            options.UpdateMode,
            options.Name,
            options.Description,
            options.ShortDescription,
            options.SupportText,
            options.ProxyFileMaxItems,
            options.WebhookMaxConnections,
            options.ProductStars,
            options.AutomaticProductCodes,
            options.StarsPerCurrencyUnit,
            options.StarsRoundingStep,
            options.TransportMode,
            proxies = options.Proxies.Select(x => new
            {
                x.Id, x.Host, x.Port, x.Username,
                passwordConfigured = x.Password.Length > 0
            }),
            effectiveProductStars,
            tokenConfigured = options.BotToken.Length > 0,
            options.BotId,
            options.BotUsername,
            options.ProvisionedAt,
            options.UpdatedAt,
            webhookUrl = options.WebhookUrl,
            avatarUrl = "/api/v1/admin/telegram/avatar",
            stats
        });
    }

    /// <summary>Показывает в админке то же встроенное изображение, которое применяется к профилю бота.</summary>
    [HttpGet("avatar")]
    public IActionResult Avatar()
    {
        var stream = typeof(TelegramBotApiClient).Assembly.GetManifestResourceStream(
            "ProxyHarbor.Api.Assets.telegram-bot-avatar.png");
        return stream is null ? NotFound() : File(stream, "image/png", enableRangeProcessing: false);
    }

    /// <summary>Проверяет token и автоматически настраивает профиль, изображение, команды и transport.</summary>
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateTelegramBotRequest request, CancellationToken token)
    {
        var current = await configurations.GetAsync(token);
        var botToken = request.BotToken is null ? current.BotToken : request.BotToken.Trim();
        if (!TelegramUpdateModes.All.Contains(request.UpdateMode, StringComparer.Ordinal) ||
            request.Name.Trim().Length is < 2 or > 64 || request.Description.Trim().Length is < 10 or > 512 ||
            request.ShortDescription.Trim().Length is < 5 or > 120 || request.SupportText.Trim().Length is < 5 or > 1000 ||
            request.ProxyFileMaxItems is < 1 or > 10_000 || request.WebhookMaxConnections is < 1 or > 100 ||
            !TelegramTokenPolicy.IsValid(botToken) ||
            !TelegramTransportModes.All.Contains(request.TransportMode, StringComparer.Ordinal))
            return Invalid("Проверьте token и ограничения полей Telegram Bot API.");
        if (request.Proxies.Count > 10 || request.Proxies.Any(x => !ValidProxy(x)))
            return Invalid("Проверьте SOCKS5-прокси: допустимо до 10 адресов с портами 1..65535.");
        if (request.TransportMode == TelegramTransportModes.Proxy && request.Proxies.Count == 0)
            return Invalid("Для режима «Только SOCKS5» добавьте хотя бы один маршрут.");
        var proxies = new List<TelegramProxyOptions>(request.Proxies.Count);
        foreach (var item in request.Proxies)
        {
            var existing = current.Proxies.SingleOrDefault(x => x.Id == item.Id);
            var password = item.Password is null ? existing?.Password ?? string.Empty : item.Password;
            if (password.Length == 0) return Invalid("Для нового SOCKS5-прокси укажите пароль.");
            proxies.Add(new TelegramProxyOptions
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                Host = item.Host.Trim().ToLowerInvariant(), Port = item.Port,
                Username = item.Username.Trim(), Password = password
            });
        }
        var catalog = await payments.GetAsync(token);
        if (request.ProductStars.Count > 10 || request.AutomaticProductCodes.Count > 10 ||
            request.StarsPerCurrencyUnit is < 0.01m or > 1_000m || request.StarsRoundingStep is < 1 or > 1_000 ||
            request.ProductStars.Any(x => !catalog.Products.ContainsKey(x.Key) || x.Value is < 1 or > 1_000_000) ||
            request.AutomaticProductCodes.Any(x => !catalog.Products.ContainsKey(x)) ||
            request.AutomaticProductCodes.Any(x => TelegramStarsPricing.Calculate(
                catalog.Products[x].AmountMinor, request.StarsPerCurrencyUnit, request.StarsRoundingStep) == 0))
            return Invalid("Проверьте тарифы, коэффициент и шаг: итоговая цена должна находиться в диапазоне 1..1000000 Stars.");

        TelegramBotIdentity identity;
        try { identity = await api.GetMeAsync(botToken, proxies, request.TransportMode, token); }
        catch (TelegramBotApiException exception)
        {
            var title = exception.ErrorCode == StatusCodes.Status504GatewayTimeout
                ? "Не удалось подключиться к Telegram API ни через один настроенный маршрут."
                : $"Telegram API отклонил запрос: {exception.Message}";
            var statusCode = exception.ErrorCode is >= 400 and <= 599 ? exception.ErrorCode : StatusCodes.Status502BadGateway;
            return Problem(title: title, detail: exception.Message, statusCode: statusCode);
        }
        var options = new TelegramBotOptions
        {
            Enabled = false,
            UpdateMode = request.UpdateMode,
            PublicBaseUrl = current.PublicBaseUrl,
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            ShortDescription = request.ShortDescription.Trim(),
            SupportText = request.SupportText.Trim(),
            ProxyFileMaxItems = request.ProxyFileMaxItems,
            WebhookMaxConnections = request.WebhookMaxConnections,
            ProductStars = new Dictionary<string, int>(request.ProductStars, StringComparer.OrdinalIgnoreCase),
            AutomaticProductCodes = new HashSet<string>(request.AutomaticProductCodes, StringComparer.OrdinalIgnoreCase),
            StarsPerCurrencyUnit = request.StarsPerCurrencyUnit,
            StarsRoundingStep = request.StarsRoundingStep,
            TransportMode = request.TransportMode,
            Proxies = proxies,
            BotToken = botToken,
            WebhookSecret = current.BotId == identity.Id && current.WebhookSecret.Length > 0
                ? current.WebhookSecret : WebhookSecret(),
            BotId = identity.Id,
            BotUsername = identity.Username
        };
        try
        {
            // Сначала полностью применяем профиль и transport в Telegram. Если внешний API
            // временно недоступен, прежняя рабочая конфигурация остаётся активной: частично
            // сохранённый снимок с Enabled=false не должен выключать бот после неудачного PUT.
            await api.ProvisionAsync(options, token);
            options.ProvisionedAt = DateTimeOffset.UtcNow;
            options.Enabled = request.Enabled;
            await configurations.SaveAsync(options, token);
        }
        catch (TelegramBotApiException exception)
        {
            return Problem(title: "Бот подключён, но автоматическая настройка не завершилась.",
                detail: exception.Message, statusCode: 502);
        }
        return await Get(token);
    }

    /// <summary>Повторно применяет профиль, команды, иконку и выбранный transport.</summary>
    [HttpPost("provision")]
    public async Task<IActionResult> Provision(CancellationToken token)
    {
        var options = await configurations.GetAsync(token);
        if (options.BotToken.Length == 0 || !options.BotId.HasValue) return Invalid("Сначала подключите bot token.");
        try { await api.ProvisionAsync(options, token); }
        catch (TelegramBotApiException exception)
        { return Problem(title: "Не удалось применить настройки Telegram.", detail: exception.Message, statusCode: 502); }
        options.ProvisionedAt = DateTimeOffset.UtcNow;
        await configurations.SaveAsync(options, token);
        return await Get(token);
    }

    /// <summary>Постраничный список Telegram-клиентов для CRM.</summary>
    [HttpGet("chats")]
    public async Task<IActionResult> Chats(
        [FromQuery, Range(1, 1_000_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        [FromQuery, StringLength(120)] string? query = null,
        CancellationToken token = default)
    {
        var source = db.TelegramChats.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            var isTelegramId = long.TryParse(term, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var telegramId);
            source = source.Where(x => x.DisplayName.Contains(term) ||
                x.Username != null && x.Username.Contains(term) || isTelegramId && x.TelegramUserId == telegramId);
        }
        var total = await source.CountAsync(token);
        var items = await source.OrderByDescending(x => x.LastInteractionAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id, x.ChatId, x.TelegramUserId, x.UserId, x.Username, x.DisplayName,
                x.LanguageCode, x.NotificationsEnabled, x.IsBlocked, x.CreatedAt, x.LastInteractionAt,
                subscription = new { x.User.Subscription!.Plan, x.User.Subscription.Status, x.User.Subscription.ExpiresAt },
                messages = db.TelegramConversationMessages.Count(m => m.TelegramChatId == x.Id)
            }).ToArrayAsync(token);
        return Ok(new { items, page, pageSize, total });
    }

    /// <summary>История одного CRM-диалога.</summary>
    [HttpGet("chats/{chatId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid chatId, [FromQuery, Range(1, 200)] int take = 100, CancellationToken token = default)
    {
        if (!await db.TelegramChats.AnyAsync(x => x.Id == chatId, token)) return NotFound();
        var items = await db.TelegramConversationMessages.AsNoTracking().Where(x => x.TelegramChatId == chatId)
            .OrderByDescending(x => x.CreatedAt).Take(take).OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Direction, x.Text, x.AdministratorId, x.OutboundMessageId, x.CreatedAt })
            .ToArrayAsync(token);
        return Ok(items);
    }

    /// <summary>Отвечает одному клиенту либо создаёт bounded broadcast всем подписанным чатам.</summary>
    [HttpPost("messages")]
    public async Task<IActionResult> Send([FromBody] SendTelegramMessageRequest request, CancellationToken token)
    {
        var administratorId = Guid.TryParse(
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var parsedAdministratorId)
            ? parsedAdministratorId : (Guid?)null;
        var text = request.Text.Trim();
        if (text.Length is < 1 or > 4096) return Invalid("Сообщение должно содержать 1..4096 символов.");
        var batch = Guid.NewGuid();
        if (request.Broadcast)
        {
            var queued = await queue.EnqueueBroadcastAsync(text, batch, administratorId, token);
            return Accepted(new { batchId = batch, queued });
        }
        if (!request.ChatId.HasValue) return Invalid("Для личного ответа укажите chatId.");
        var target = await db.TelegramChats.SingleOrDefaultAsync(x => x.Id == request.ChatId, token);
        if (target is null) return NotFound();
        if (target.IsBlocked) return Invalid("Чат заблокирован или пользователь остановил бота.");
        var messageId = await queue.EnqueueTextAsync(target, text, $"admin:{batch:N}", "admin", administratorId, token: token);
        return Accepted(new { messageId });
    }

    /// <summary>Меняет сервисное состояние CRM-чата.</summary>
    [HttpPut("chats/{chatId:guid}")]
    public async Task<IActionResult> UpdateChat(Guid chatId, [FromBody] UpdateTelegramChatRequest request, CancellationToken token)
    {
        var chat = await db.TelegramChats.SingleOrDefaultAsync(x => x.Id == chatId, token);
        if (chat is null) return NotFound();
        chat.NotificationsEnabled = request.NotificationsEnabled;
        chat.IsBlocked = request.IsBlocked;
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private static string WebhookSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool ValidProxy(UpdateTelegramProxyRequest value) =>
        value.Id != Guid.Empty && value.Host.Trim().Length is >= 1 and <= 253 &&
        Uri.CheckHostName(value.Host.Trim()) != UriHostNameType.Unknown &&
        value.Port is >= 1 and <= 65_535 && value.Username.Trim().Length is >= 1 and <= 128 &&
        value.Username.All(x => !char.IsControl(x)) &&
        (value.Password is null || value.Password.Length is >= 1 and <= 256 && value.Password.All(x => !char.IsControl(x)));

    private static BadRequestObjectResult Invalid(string title) => new(new ProblemDetails { Title = title, Status = 400 });
}

/// <summary>Полный снимок настроек торгового бота; null token сохраняет прежний.</summary>
public sealed class UpdateTelegramBotRequest
{
    /// <summary>Включить runtime после успешной настройки.</summary>
    public bool Enabled { get; set; }
    /// <summary>webhook или polling.</summary>
    [Required, StringLength(16)] public string UpdateMode { get; set; } = TelegramUpdateModes.Webhook;
    /// <summary>Имя профиля.</summary>
    [Required, StringLength(64, MinimumLength = 2)] public string Name { get; set; } = "ProxyHarbor";
    /// <summary>Полное описание.</summary>
    [Required, StringLength(512, MinimumLength = 10)] public string Description { get; set; } = string.Empty;
    /// <summary>Краткое описание.</summary>
    [Required, StringLength(120, MinimumLength = 5)] public string ShortDescription { get; set; } = string.Empty;
    /// <summary>Ответ при передаче вопроса оператору.</summary>
    [Required, StringLength(1000, MinimumLength = 5)] public string SupportText { get; set; } = string.Empty;
    /// <summary>Максимум прокси в одном файле.</summary>
    [Range(1, 10_000)] public int ProxyFileMaxItems { get; set; } = 1000;
    /// <summary>Параллелизм webhook.</summary>
    [Range(1, 100)] public int WebhookMaxConnections { get; set; } = 20;
    /// <summary>Цены продуктов в Telegram Stars.</summary>
    [Required] public Dictionary<string, int> ProductStars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Тарифы, цену которых нужно автоматически синхронизировать с основным каталогом.</summary>
    [Required] public HashSet<string> AutomaticProductCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Количество Stars на одну целую единицу валюты тарифа.</summary>
    [Range(typeof(decimal), "0.01", "1000")] public decimal StarsPerCurrencyUnit { get; set; } = 1m;
    /// <summary>Шаг безопасного округления автоматической цены вверх.</summary>
    [Range(1, 1_000)] public int StarsRoundingStep { get; set; } = 5;
    /// <summary>Новый token; null сохраняет прежний.</summary>
    [StringLength(256)] public string? BotToken { get; set; }
    /// <summary>auto, proxy или direct.</summary>
    [Required, StringLength(16)] public string TransportMode { get; set; } = TelegramTransportModes.Auto;
    /// <summary>Полный упорядоченный список SOCKS5 upstream; null password сохраняет существующий.</summary>
    [Required, MaxLength(10)] public List<UpdateTelegramProxyRequest> Proxies { get; set; } = [];
}

/// <summary>Редактируемые параметры SOCKS5-маршрута без раскрытия сохранённого пароля.</summary>
public sealed class UpdateTelegramProxyRequest
{
    /// <summary>Стабильный идентификатор настройки.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Имя хоста или IP SOCKS5-сервера.</summary>
    [Required, StringLength(253, MinimumLength = 1)] public string Host { get; set; } = string.Empty;
    /// <summary>TCP-порт SOCKS5-сервера.</summary>
    [Range(1, 65_535)] public int Port { get; set; } = 1080;
    /// <summary>Логин SOCKS5-сервера.</summary>
    [Required, StringLength(128, MinimumLength = 1)] public string Username { get; set; } = string.Empty;
    /// <summary>Новый пароль либо null для сохранения ранее записанного.</summary>
    [StringLength(256, MinimumLength = 1)] public string? Password { get; set; }
}

/// <summary>Ручное сообщение CRM или broadcast.</summary>
public sealed class SendTelegramMessageRequest
{
    /// <summary>Диалог для личного ответа.</summary>
    public Guid? ChatId { get; set; }
    /// <summary>Отправить всем подписанным диалогам.</summary>
    public bool Broadcast { get; set; }
    /// <summary>Текст сообщения.</summary>
    [Required, StringLength(4096, MinimumLength = 1)] public string Text { get; set; } = string.Empty;
}

/// <summary>Управление подпиской на рассылки и блокировкой чата.</summary>
public sealed class UpdateTelegramChatRequest
{
    /// <summary>Разрешены ли сервисные уведомления.</summary>
    public bool NotificationsEnabled { get; set; }
    /// <summary>Отключена ли исходящая доставка.</summary>
    public bool IsBlocked { get; set; }
}
