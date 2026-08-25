using Microsoft.Extensions.Configuration;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует fail-closed и exact-match семантику браузерного входа.</summary>
public sealed class AdminCredentialValidatorTests
{
    private const string Password = "integration-admin-key-at-least-24-chars";

    [Fact]
    public void ExactUsernameAndPasswordAuthenticate()
    {
        var validator = Create("harbor-admin", Password);

        Assert.True(validator.Validate("harbor-admin", Password));
        Assert.False(validator.Validate("Harbor-admin", Password));
        Assert.False(validator.Validate("harbor-admin", Password + "-suffix"));
        Assert.False(validator.Validate("harbor-admin", "wrong"));
    }

    [Fact]
    public void MissingInvalidAndOversizedCredentialsFailClosed()
    {
        Assert.False(Create(null, Password).Validate("admin", Password));
        Assert.False(Create("bad username", Password).Validate("bad username", Password));
        Assert.False(Create("admin", "short").Validate("admin", "short"));
        Assert.False(Create("admin", Password).Validate(new string('a', 65), Password));
        Assert.False(Create("admin", Password).Validate("admin", new string('x', 257)));
        Assert.False(Create("admin", Password).Validate(null, null));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("proxy.harbor_admin-01")]
    public void UsernamePolicyAcceptsPortableNames(string username) =>
        Assert.True(AdminUsernamePolicy.IsValid(username));

    private static AdminCredentialValidator Create(string? username, string? password)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminUsername"] = username,
            ["Security:AdminPassword"] = password
        }).Build();
        return new AdminCredentialValidator(configuration);
    }
}
