using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует семантику изменения пользовательских и встроенных feed'ов.</summary>
public sealed class AdminSourceControllerTests
{
    [Theory]
    [InlineData("   ", ProxyProtocol.Http, "Name")]
    [InlineData("Valid source", (ProxyProtocol)999, "Protocol")]
    public void RequestValidationRejectsWhitespaceNameAndUnknownProtocol(
        string name,
        ProxyProtocol protocol,
        string expectedMember)
    {
        var request = new SourceRequest(name, "https://8.8.8.8/feed.txt", protocol);
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(expectedMember, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CaseSensitivePathChangeResetsSourceHealthAndBackoff()
    {
        var options = Options($"source-path-{Guid.NewGuid():N}");
        var source = new ProxySource
        {
            Name = "Custom",
            Url = "https://8.8.8.8/Feed.txt",
            LastFetchedAt = DateTimeOffset.UtcNow,
            LastSucceededAt = DateTimeOffset.UtcNow,
            NextFetchAt = DateTimeOffset.UtcNow.AddHours(1),
            LastItemCount = 10,
            ConsecutiveFailures = 3,
            LastError = "old failure"
        };
        await SeedAsync(options, source);
        var controller = Controller(options);

        var result = await controller.UpdateSource(source.Id,
            new SourceRequest("Custom", "https://8.8.8.8/feed.txt", ProxyProtocol.Http),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await using var verify = new ProxyHarborDbContext(options);
        var updated = await verify.Sources.SingleAsync();
        Assert.Equal("https://8.8.8.8/feed.txt", updated.Url);
        Assert.Null(updated.LastFetchedAt);
        Assert.Null(updated.LastSucceededAt);
        Assert.Null(updated.NextFetchAt);
        Assert.Equal(0, updated.LastItemCount);
        Assert.Equal(0, updated.ConsecutiveFailures);
        Assert.Null(updated.LastError);
    }

    [Fact]
    public async Task BuiltInSourceAllowsToggleWithoutMutableMetadata()
    {
        var definition = BuiltInSourceCatalog.Sources[0];
        var options = Options($"source-built-in-{Guid.NewGuid():N}");
        var source = new ProxySource
        {
            Name = definition.Name,
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            Priority = definition.Rank * 10,
            Enabled = true,
            LastItemCount = 123
        };
        await SeedAsync(options, source);
        var controller = Controller(options);

        var result = await controller.UpdateSource(source.Id,
            new SourceRequest(definition.Name, definition.Url, definition.Protocol, definition.Rank * 10, Enabled: false),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await using var verify = new ProxyHarborDbContext(options);
        var updated = await verify.Sources.SingleAsync();
        Assert.False(updated.Enabled);
        Assert.Equal(123, updated.LastItemCount);
    }

    [Fact]
    public async Task BuiltInSourceRejectsMetadataDrift()
    {
        var definition = BuiltInSourceCatalog.Sources[0];
        var options = Options($"source-built-in-drift-{Guid.NewGuid():N}");
        var source = new ProxySource
        {
            Name = definition.Name,
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            Priority = definition.Rank * 10
        };
        await SeedAsync(options, source);
        var controller = Controller(options);

        var result = await controller.UpdateSource(source.Id,
            new SourceRequest("Changed built-in", definition.Url, definition.Protocol, definition.Rank * 10),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, Assert.IsType<ProblemDetails>(conflict.Value).Status);
        await using var verify = new ProxyHarborDbContext(options);
        Assert.Equal(definition.Name, (await verify.Sources.SingleAsync()).Name);
    }

    private static DbContextOptions<ProxyHarborDbContext> Options(string database) =>
        new DbContextOptionsBuilder<ProxyHarborDbContext>().UseInMemoryDatabase(database).Options;

    private static async Task SeedAsync(DbContextOptions<ProxyHarborDbContext> options, ProxySource source)
    {
        await using var db = new ProxyHarborDbContext(options);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
    }

    private static AdminController Controller(DbContextOptions<ProxyHarborDbContext> options) =>
        new(new TestDbFactory(options), null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new BackupOptions()),
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions()));

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
