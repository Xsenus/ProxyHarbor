using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyHarbor.Api;

/// <summary>Безопасный минимальный клиент официального Telegram Bot API.</summary>
public sealed class TelegramBotApiClient(IHttpClientFactory clients)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal Task<TelegramBotIdentity> GetMeAsync(string botToken, CancellationToken token) =>
        GetMeAsync(botToken, [], TelegramTransportModes.Direct, token);

    /// <summary>Проверяет token и возвращает Telegram identity бота.</summary>
    public async Task<TelegramBotIdentity> GetMeAsync(
        string botToken, IReadOnlyList<TelegramProxyOptions> proxies, string transportMode, CancellationToken token)
    {
        var result = await CallAsync(new TelegramBotOptions
        {
            BotToken = botToken,
            Proxies = proxies.ToList(),
            TransportMode = transportMode
        }, "getMe", new { }, token);
        if (!result.TryGetProperty("id", out var id) || !id.TryGetInt64(out var botId) ||
            !result.TryGetProperty("username", out var username) || string.IsNullOrWhiteSpace(username.GetString()))
            throw new TelegramBotApiException(502, "Telegram вернул неполную identity бота.");
        return new TelegramBotIdentity(botId, username.GetString()!);
    }

    /// <summary>Атомарно приводит профиль, команды, menu button и транспорт к настройкам ProxyHarbor.</summary>
    public async Task ProvisionAsync(TelegramBotOptions options, CancellationToken token)
    {
        _ = await CallAsync(options, "setMyName", new { name = options.Name }, token);
        _ = await CallAsync(options, "setMyDescription", new { description = options.Description }, token);
        _ = await CallAsync(options, "setMyShortDescription", new { short_description = options.ShortDescription }, token);
        var commands = LocalizedCommands().ToArray();
        _ = await CallAsync(options, "setMyCommands", new { commands = commands[0].Commands }, token);
        foreach (var localized in commands)
            _ = await CallAsync(options, "setMyCommands", new { commands = localized.Commands, language_code = localized.Language }, token);
        _ = await CallAsync(options, "setChatMenuButton", new { menu_button = new { type = "commands" } }, token);
        await SetEmbeddedAvatarAsync(options, token);

        if (options.UpdateMode == TelegramUpdateModes.Webhook)
        {
            _ = await CallAsync(options, "setWebhook", new
            {
                url = options.WebhookUrl,
                secret_token = options.WebhookSecret,
                max_connections = options.WebhookMaxConnections,
                allowed_updates = AllowedUpdates,
                drop_pending_updates = false
            }, token);
        }
        else
        {
            _ = await CallAsync(options, "deleteWebhook", new { drop_pending_updates = false }, token);
        }
    }

    private static IEnumerable<(string Language, object[] Commands)> LocalizedCommands()
    {
        yield return ("ru", Commands("Открыть главное меню", "Личный кабинет и подписка", "Купить или продлить подписку", "Получить файл с прокси", "Настроить уведомления", "Выбрать язык", "Написать в поддержку", "Помощь и ответы"));
        yield return ("en", Commands("Open the main menu", "Account and subscription", "Buy or renew a subscription", "Get a proxy file", "Configure notifications", "Choose language", "Contact support", "Help and answers"));
        yield return ("de", Commands("Hauptmenü öffnen", "Konto und Abonnement", "Abonnement kaufen oder verlängern", "Proxy-Datei abrufen", "Benachrichtigungen einstellen", "Sprache auswählen", "Support kontaktieren", "Hilfe und Antworten"));
        yield return ("fr", Commands("Ouvrir le menu principal", "Compte et abonnement", "Acheter ou renouveler", "Obtenir un fichier de proxys", "Configurer les notifications", "Choisir la langue", "Contacter l'assistance", "Aide et réponses"));
        yield return ("zh", Commands("打开主菜单", "账户与订阅", "购买或续订", "获取代理文件", "设置通知", "选择语言", "联系客服", "帮助与解答"));
    }

    private static object[] Commands(string start, string account, string buy, string proxies, string notifications, string language, string support, string help) =>
    [
        new { command = "start", description = start }, new { command = "account", description = account },
        new { command = "buy", description = buy }, new { command = "proxies", description = proxies },
        new { command = "notifications", description = notifications }, new { command = "language", description = language },
        new { command = "support", description = support }, new { command = "help", description = help }
    ];

    /// <summary>Получает update long polling; вызывается только polling worker.</summary>
    public async Task<JsonElement[]> GetUpdatesAsync(
        TelegramBotOptions options, long offset, CancellationToken token)
    {
        var result = await CallAsync(options, "getUpdates", new
        {
            offset,
            limit = 100,
            timeout = 30,
            allowed_updates = AllowedUpdates
        }, token);
        return result.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    /// <summary>Отправляет текст с опциональной inline-клавиатурой.</summary>
    public async Task<long> SendMessageAsync(
        TelegramBotOptions options, long chatId, string text, object? replyMarkup, CancellationToken token)
    {
        var result = await CallAsync(options, "sendMessage", new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = replyMarkup
        }, token);
        return result.GetProperty("message_id").GetInt64();
    }

    /// <summary>Отправляет счёт цифровой услуги в Telegram Stars.</summary>
    public async Task<long> SendStarsInvoiceAsync(
        TelegramBotOptions options, long chatId, TelegramInvoicePayload invoice, CancellationToken token)
    {
        var result = await CallAsync(options, "sendInvoice", new
        {
            chat_id = chatId,
            title = invoice.Title,
            description = invoice.Description,
            payload = invoice.OrderId.ToString("N"),
            provider_token = string.Empty,
            currency = "XTR",
            prices = new[] { new { label = invoice.Title, amount = invoice.Stars } },
            start_parameter = $"order_{invoice.OrderId:N}",
            protect_content = true
        }, token);
        return result.GetProperty("message_id").GetInt64();
    }

    /// <summary>Подтверждает или отклоняет обязательный pre-checkout за отведённые Telegram 10 секунд.</summary>
    public Task AnswerPreCheckoutAsync(
        TelegramBotOptions options, string queryId, bool ok, string? error, CancellationToken token) =>
        CallBooleanAsync(options, "answerPreCheckoutQuery", new
        {
            pre_checkout_query_id = queryId,
            ok,
            error_message = ok ? null : error
        }, token);

    /// <summary>Закрывает индикатор callback-кнопки.</summary>
    public Task AnswerCallbackAsync(TelegramBotOptions options, string queryId, string? text, CancellationToken token)
    {
        // Telegram трактует отсутствие текста как обычное закрытие индикатора кнопки.
        // Явный JSON null некоторые клиенты показывают пользователю как строку "null".
        object payload = string.IsNullOrWhiteSpace(text)
            ? new { callback_query_id = queryId }
            : new { callback_query_id = queryId, text, show_alert = false };
        return CallBooleanAsync(options, "answerCallbackQuery", payload, token);
    }

    /// <summary>Отправляет сгенерированный текстовый файл прокси без записи на диск.</summary>
    public async Task<long> SendDocumentAsync(
        TelegramBotOptions options, long chatId, string fileName, byte[] content, string caption, CancellationToken token)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        multipart.Add(new StringContent(caption), "caption");
        multipart.Add(new StringContent("true"), "protect_content");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("text/plain");
        multipart.Add(file, "document", fileName);
        var result = await SendAsync(options, "sendDocument", multipart, token);
        return result.GetProperty("message_id").GetInt64();
    }

    private async Task SetEmbeddedAvatarAsync(TelegramBotOptions options, CancellationToken token)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "ProxyHarbor.Api.Assets.telegram-bot-avatar.png")
            ?? throw new InvalidOperationException("Встроенная иконка Telegram-бота не найдена.");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(
            new { type = "static", photo = "attach://avatar" }, Json)), "photo");
        var image = new ByteArrayContent(memory.ToArray());
        image.Headers.ContentType = new("image/png");
        multipart.Add(image, "avatar", "proxyharbor-bot.png");
        _ = await SendAsync(options, "setMyProfilePhoto", multipart, token);
    }

    private async Task CallBooleanAsync(TelegramBotOptions options, string method, object payload, CancellationToken token) =>
        _ = await CallAsync(options, method, payload, token);

    private async Task<JsonElement> CallAsync(
        TelegramBotOptions options, string method, object payload, CancellationToken token)
    {
        using var content = JsonContent.Create(payload, options: Json);
        return await SendAsync(options, method, content, token);
    }

    private async Task<JsonElement> SendAsync(
        TelegramBotOptions options, string method, HttpContent content, CancellationToken token)
    {
        var tokenValue = options.BotToken;
        if (!TelegramTokenPolicy.IsValid(tokenValue))
            throw new TelegramBotApiException(400, "Некорректный token Telegram-бота.");
        var payload = await content.ReadAsByteArrayAsync(token);
        var contentHeaders = content.Headers.ToArray();
        var attempts = ConnectionAttempts(options).ToArray();
        Exception? lastTransportError = null;
        HttpResponseMessage? response = null;
        foreach (var proxy in attempts)
        {
            try
            {
                using var requestContent = new ByteArrayContent(payload);
                foreach (var header in contentHeaders)
                    requestContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response = await PostAsync(proxy, tokenValue, method, requestContent, token);
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                lastTransportError = exception;
            }
        }
        if (response is null)
            throw new TelegramBotApiException(504,
                attempts.Any(x => x is not null)
                    ? "Не удалось подключиться к Telegram ни через один настроенный SOCKS5-прокси."
                    : "Сервер не может установить соединение с Telegram Bot API.",
                innerException: lastTransportError);
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            TelegramEnvelope? envelope = null;
            try { envelope = JsonSerializer.Deserialize<TelegramEnvelope>(body, Json); }
            catch (JsonException) { /* Ошибка ниже не включает body и token. */ }
            if (!response.IsSuccessStatusCode || envelope is null || !envelope.Ok)
                throw new TelegramBotApiException(
                    envelope?.ErrorCode ?? (int)response.StatusCode,
                    TelegramSafeText(envelope?.Description),
                    envelope?.Parameters?.RetryAfter,
                    response.StatusCode == HttpStatusCode.Forbidden);
            return envelope.Result;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        TelegramProxyOptions? proxy, string botToken, string method, HttpContent content, CancellationToken token)
    {
        if (proxy is null)
            return await clients.CreateClient("telegram").PostAsync(
                $"https://api.telegram.org/bot{botToken}/{method}", content, token);
        var webProxy = new WebProxy(new Uri($"socks5://{proxy.Host}:{proxy.Port}"))
        {
            Credentials = new NetworkCredential(proxy.Username, proxy.Password)
        };
        using var handler = new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await client.PostAsync($"https://api.telegram.org/bot{botToken}/{method}", content, token);
    }

    internal static IEnumerable<TelegramProxyOptions?> ConnectionAttempts(TelegramBotOptions options)
    {
        if (options.TransportMode != TelegramTransportModes.Direct)
            foreach (var proxy in options.Proxies) yield return proxy;
        if (options.TransportMode != TelegramTransportModes.Proxy) yield return null;
    }

    private static string TelegramSafeText(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Telegram Bot API отклонил запрос.";
        var normalized = new string(description.Where(x => !char.IsControl(x)).Take(500).ToArray());
        return normalized.Length == 0 ? "Telegram Bot API отклонил запрос." : normalized;
    }

    private static readonly string[] AllowedUpdates =
        ["message", "callback_query", "pre_checkout_query", "my_chat_member"];

    private sealed class TelegramEnvelope
    {
        public bool Ok { get; set; }
        public JsonElement Result { get; set; }
        [JsonPropertyName("error_code")] public int? ErrorCode { get; set; }
        public string? Description { get; set; }
        public TelegramResponseParameters? Parameters { get; set; }
    }

    private sealed class TelegramResponseParameters
    {
        [JsonPropertyName("retry_after")] public int? RetryAfter { get; set; }
    }
}

/// <summary>Identity, возвращённая getMe.</summary>
public sealed record TelegramBotIdentity(long Id, string Username);

/// <summary>Доверенный payload задания invoice.</summary>
public sealed record TelegramInvoicePayload(Guid OrderId, string Title, string Description, int Stars);

/// <summary>Ошибка Bot API с данными для управляемого повтора.</summary>
public sealed class TelegramBotApiException(
    int errorCode, string message, int? retryAfterSeconds = null, bool forbidden = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>Telegram error_code либо HTTP status.</summary>
    public int ErrorCode { get; } = errorCode;
    /// <summary>Предписанный сервером интервал повторения.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    /// <summary>Бот заблокирован пользователем.</summary>
    public bool Forbidden { get; } = forbidden;
    /// <summary>Можно ли безопасно повторить операцию.</summary>
    public bool Transient => ErrorCode == 429 || ErrorCode >= 500;
}

/// <summary>Строгая validation token до включения его в URI Bot API.</summary>
public static class TelegramTokenPolicy
{
    /// <summary>Проверяет bounded path-safe синтаксис token без сетевого вызова.</summary>
    public static bool IsValid(string? value) => value is { Length: >= 20 and <= 256 } &&
        value.Count(x => x == ':') == 1 &&
        value.All(x => char.IsAsciiLetterOrDigit(x) || x is ':' or '_' or '-');
}
