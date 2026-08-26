using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using ProxyHarbor.Infrastructure;
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
    Task SendPasswordResetAsync(string email, string token, string language, CancellationToken cancellationToken);
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
    public async Task SendPasswordResetAsync(string email, string token, string language, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("SMTP для восстановления пароля не настроен.");
        var link = $"{_options.PublicBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        var safeLink = HtmlEncoder.Default.Encode(link);
        var content = PasswordResetContent.For(language);
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = content.Subject,
            Body = $"<p>{content.Intro}</p><p><a href=\"{safeLink}\">{content.Action}</a></p><p>{content.Ignore}</p>",
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

    private sealed record PasswordResetContent(string Subject, string Intro, string Action, string Ignore)
    {
        public static PasswordResetContent For(string language) => SupportedLanguages.Normalize(language) switch
        {
            SupportedLanguages.English => new("Reset your ProxyHarbor password", "We received a request to reset your ProxyHarbor password.", "Set a new password", "If this was not you, simply ignore this email."),
            SupportedLanguages.German => new("ProxyHarbor-Passwort zurücksetzen", "Wir haben eine Anfrage zum Zurücksetzen Ihres ProxyHarbor-Passworts erhalten.", "Neues Passwort festlegen", "Falls Sie dies nicht waren, ignorieren Sie diese E-Mail."),
            SupportedLanguages.French => new("Réinitialiser votre mot de passe ProxyHarbor", "Nous avons reçu une demande de réinitialisation de votre mot de passe ProxyHarbor.", "Définir un nouveau mot de passe", "Si vous n'êtes pas à l'origine de cette demande, ignorez cet e-mail."),
            SupportedLanguages.Chinese => new("重置 ProxyHarbor 密码", "我们收到了重置您的 ProxyHarbor 密码的请求。", "设置新密码", "如果这不是您的操作，请忽略此邮件。"),
            _ => new("Восстановление пароля ProxyHarbor", "Получен запрос на восстановление пароля ProxyHarbor.", "Задать новый пароль", "Если это были не вы, просто проигнорируйте письмо.")
        };
    }
}
