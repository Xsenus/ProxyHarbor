using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует семантику изменения пользовательских и встроенных feed'ов.</summary>
[Collection(PostgresIntegrationGroup.Name)]
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
    public async Task CreateSourcePersistsNormalizedRequestAndRejectsDuplicate()
    {
        var options = Options($"source-create-{Guid.NewGuid():N}");
        var controller = Controller(options);
        var request = new SourceRequest(
            "  Custom feed  ", "HTTPS://8.8.8.8/feed.txt", ProxyProtocol.Socks5, Priority: 42, Enabled: false);

        var created = await controller.CreateSource(request, CancellationToken.None);

        var action = Assert.IsType<CreatedAtActionResult>(created.Result);
        Assert.Equal(nameof(AdminController.GetSource), action.ActionName);
        var source = Assert.IsType<SourceResponse>(action.Value);
        Assert.Equal("Custom feed", source.Name);
        Assert.Equal("https://8.8.8.8/feed.txt", source.Url);
        Assert.Equal(ProxyProtocol.Socks5, source.DefaultProtocol);
        Assert.Equal(42, source.Priority);
        Assert.False(source.Enabled);
        await using var verify = new ProxyHarborDbContext(options);
        Assert.Equal(source.Id, (await verify.Sources.SingleAsync()).Id);

        var fetched = await controller.GetSource(source.Id, CancellationToken.None);
        Assert.Equal(source, Assert.IsType<OkObjectResult>(fetched.Result).Value);
        Assert.IsType<NotFoundResult>((await controller.GetSource(Guid.NewGuid(), CancellationToken.None)).Result);

        var duplicate = await controller.CreateSource(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(duplicate.Result);
        Assert.Equal(409, Assert.IsType<ProblemDetails>(conflict.Value).Status);
        Assert.Equal(1, await verify.Sources.CountAsync());
    }

    [Fact]
    public async Task DeleteRemovesCustomSourceButOnlyDisablesBuiltInSource()
    {
        var definition = BuiltInSourceCatalog.Sources[0];
        var options = Options($"source-delete-{Guid.NewGuid():N}");
        var custom = new ProxySource { Name = "Custom", Url = "https://8.8.8.8/custom.txt" };
        var builtIn = new ProxySource
        {
            Name = definition.Name,
            Url = definition.Url,
            DefaultProtocol = definition.Protocol,
            Enabled = true
        };
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.AddRange(custom, builtIn);
            await seed.SaveChangesAsync();
        }
        var controller = Controller(options);

        Assert.IsType<NoContentResult>(await controller.DeleteSource(custom.Id, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.DeleteSource(builtIn.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DeleteSource(Guid.NewGuid(), CancellationToken.None));

        await using var verify = new ProxyHarborDbContext(options);
        var remaining = await verify.Sources.SingleAsync();
        Assert.Equal(builtIn.Id, remaining.Id);
        Assert.False(remaining.Enabled);
    }

    [Fact]
    public async Task SourcesAreOrderedByPriorityAndExposeCatalogMetadata()
    {
        var definition = BuiltInSourceCatalog.Sources[0];
        var options = Options($"source-list-{Guid.NewGuid():N}");
        await using (var seed = new ProxyHarborDbContext(options))
        {
            seed.Sources.AddRange(
                new ProxySource { Name = "Later", Url = "https://8.8.8.8/later.txt", Priority = 100 },
                new ProxySource
                {
                    Name = definition.Name,
                    Url = definition.Url,
                    DefaultProtocol = definition.Protocol,
                    Priority = 10
                });
            await seed.SaveChangesAsync();
        }

        var result = await Controller(options).Sources(CancellationToken.None);

        var responses = Assert.IsType<OkObjectResult>(result.Result).Value;
        var ordered = Assert.IsType<SourceResponse[]>(responses);
        Assert.True(ordered[0].IsBuiltIn);
        Assert.Equal(definition.Provider, ordered[0].Provider);
        Assert.False(ordered[1].IsBuiltIn);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentCreatesReturnCreatedAndConflictInsteadOfLeakingUniqueViolation()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_source_race_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var setupOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            await using (var setup = new ProxyHarborDbContext(setupOptions))
                await DatabaseSeeder.InitializeAsync(setup);
            var interceptor = new ConcurrentSourceInsertInterceptor();
            var options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
            var request = new SourceRequest("Concurrent", "https://8.8.8.8/concurrent.txt", ProxyProtocol.Http);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var results = await Task.WhenAll(
                Controller(options).CreateSource(request, timeout.Token),
                Controller(options).CreateSource(request, timeout.Token));

            Assert.Single(results, result => result.Result is CreatedAtActionResult);
            Assert.Single(results, result => result.Result is ConflictObjectResult);
            await using var verify = new ProxyHarborDbContext(options);
            Assert.Equal(1, await verify.Sources.CountAsync(source => source.Url == "https://8.8.8.8/concurrent.txt"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
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
            LastResultTruncated = true,
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
        Assert.False(updated.LastResultTruncated);
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

    /// <summary>Останавливает оба INSERT до их выполнения, гарантируя гонку после предварительных SELECT.</summary>
    private sealed class ConcurrentSourceInsertInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"Sources\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _bothArrived.TrySetResult();
                await _bothArrived.Task.WaitAsync(cancellationToken);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
