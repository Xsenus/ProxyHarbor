using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace ProxyHarbor.Api;

/// <summary>Настройки SMTP, не содержащие секретов в исходном коде.</summary>
public sealed class AccountEmailOptions
{
    /// <summary>Имя configuration section.</summary>
    public const string Section = "Email";
    /// <summary>DNS-имя SMTP relay.</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>TCP-порт SMTP relay.</summary>
    public int Port { get; set; } = 587;
    /// <summary>Требовать TLS при отправке.</summary>
    public bool UseSsl { get; set; } = true;
    /// <summary>Имя SMTP-пользователя.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Пароль, загружаемый из отдельного secret-файла.</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>Проверенный адрес отправителя.</summary>
    public string FromAddress { get; set; } = string.Empty;
    /// <summary>Отображаемое имя отправителя.</summary>
    public string FromName { get; set; } = "ProxyHarbor";
    /// <summary>Публичный HTTPS origin для ссылок восстановления.</summary>
    public string PublicBaseUrl { get; set; } = "https://proxy.blagodaty.ru";
}

/// <summary>Абстракция позволяет тестировать reset-flow без отправки реальных писем.</summary>
public interface IAccountEmailSender
{
    /// <summary>Все обязательные параметры транспорта присутствуют.</summary>
    bool IsConfigured { get; }
    /// <summary>Отправляет одноразовый token только указанному получателю.</summary>
    Task SendPasswordResetAsync(string email, string token, CancellationToken cancellationToken);
}

/// <summary>Отправляет короткое письмо восстановления через настроенный SMTP relay.</summary>
public sealed class SmtpAccountEmailSender(IOptions<AccountEmailOptions> options) : IAccountEmailSender
{
    private readonly AccountEmailOptions _options = options.Value;
    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Host) &&
        !string.IsNullOrWhiteSpace(_options.FromAddress) &&
        !string.IsNullOrWhiteSpace(_options.Username) &&
        !string.IsNullOrWhiteSpace(_options.Password) &&
        Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var baseUri) && baseUri.Scheme == Uri.UriSchemeHttps;

    /// <inheritdoc />
    public async Task SendPasswordResetAsync(string email, string token, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("SMTP для восстановления пароля не настроен.");
        var link = $"{_options.PublicBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        var safeLink = HtmlEncoder.Default.Encode(link);
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = "Восстановление пароля ProxyHarbor",
            Body = $"<p>Получен запрос на восстановление пароля ProxyHarbor.</p><p><a href=\"{safeLink}\">Задать новый пароль</a></p><p>Если это были не вы, просто проигнорируйте письмо.</p>",
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(email));
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
