using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupCatalogSigningOptionsTests
{
    [Fact]
    public void AbsentKeyLeavesPublicationUnavailable()
    {
        Assert.True(BackupCatalogSigningOptions.IsValid(new()));
        Assert.False(BackupCatalogSigningOptions.IsValid(new() { KeyReference = "unsafe/ref" }));
    }

    [Theory]
    [InlineData("short", "catalog-v1")]
    [InlineData("valid-separate-signing-key-32-characters", "unsafe/ref")]
    [InlineData("valid-separate-signing-key-32-characters", "")]
    public void InvalidKeyOrReferenceFailsClosed(string key, string reference)
    {
        Assert.False(BackupCatalogSigningOptions.IsValid(new()
        {
            SigningKey = key,
            KeyReference = reference
        }));
    }

    [Fact]
    public void StrongSeparateKeyAndSafeReferenceAreAccepted()
    {
        Assert.True(BackupCatalogSigningOptions.IsValid(new()
        {
            SigningKey = "valid-separate-signing-key-32-characters",
            KeyReference = "catalog-v2"
        }));
    }

    [Fact]
    public void InfrastructureBindingRejectsMalformedExplicitKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=fixture;Username=fixture",
                ["BackupCatalogSigning:SigningKey"] = "short",
                ["BackupCatalogSigning:KeyReference"] = "catalog-v1"
            }).Build();
        var services = new ServiceCollection();
        services.AddProxyHarborInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BackupCatalogSigningOptions>>().Value);
    }
}
