using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api;
using ProxyHarbor.Api.Controllers;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует bounded-контракты аккаунтов и fail-closed SMTP configuration.</summary>
public sealed class AuthControllerTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("admin@proxy.example")]
    public void LoginIdentifierAcceptsUsernameOrEmail(string identifier)
    {
        var request = new AccountLoginRequest { Username = identifier, Password = "some-password" };

        Assert.Empty(Validate(request));
        Assert.True(request.RememberMe);
    }

    [Fact]
    public void TokenLoginAlsoRemembersTheBrowserByDefault()
    {
        var request = new TokenLoginRequest { Token = new string('a', 80) };

        Assert.Empty(Validate(request));
        Assert.True(request.RememberMe);
    }

    [Fact]
    public void RegistrationRequiresPortableUsernameEmailAndLongPassword()
    {
        var invalid = new RegisterAccountRequest { Username = "bad user", Email = "not-email", Password = "short" };
        var valid = new RegisterAccountRequest { Username = "proxy.user", Email = "user@example.com", DisplayName = "Proxy User", Password = "Long-password-123!" };

        Assert.NotEmpty(Validate(invalid));
        Assert.Empty(Validate(valid));
    }

    [Fact]
    public void EmailSenderIsDisabledUntilEverySecretIsConfigured()
    {
        var incomplete = new SmtpAccountEmailSender(Options.Create(new AccountEmailOptions
        {
            Host = "smtp.example.com",
            Username = "proxy",
            FromAddress = "noreply@example.com"
        }));
        var complete = new SmtpAccountEmailSender(Options.Create(new AccountEmailOptions
        {
            Host = "smtp.example.com",
            Username = "proxy",
            Password = "secret",
            FromAddress = "noreply@example.com",
            PublicBaseUrl = "https://proxy.example.com"
        }));

        Assert.False(incomplete.IsConfigured);
        Assert.True(complete.IsConfigured);
    }

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }
}
