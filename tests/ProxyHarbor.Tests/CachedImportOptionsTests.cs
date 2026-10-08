using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class CachedImportOptionsTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(86400, true)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(29, false)]
    [InlineData(86401, false)]
    public void CachedImportIntervalIsOptInAndBounded(int seconds, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test",
            ["Collector:CachedImportIntervalSeconds"] = seconds.ToString(CultureInfo.InvariantCulture)
        }).Build();
        using var provider = new ServiceCollection().AddLogging()
            .AddProxyHarborInfrastructure(configuration).BuildServiceProvider();
        if (valid) Assert.Equal(seconds, provider.GetRequiredService<IOptions<CollectorOptions>>().Value.CachedImportIntervalSeconds);
        else Assert.Contains(Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<CollectorOptions>>().Value).Failures,
            failure => failure.Contains(nameof(CollectorOptions.CachedImportIntervalSeconds), StringComparison.Ordinal));
    }
}
