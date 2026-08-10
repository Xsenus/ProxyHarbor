using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

/// <summary>Доказывает полный lifecycle collection audit и bulk-upsert на настоящей PostgreSQL.</summary>
[Collection(PostgresIntegrationGroup.Name)]
public sealed class ProxyCollectorIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SuccessfulAndCancelledCyclesRecoverAndFinalizeEveryAuditRow()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_collector_audit_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var abandonedId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var expiredValidationRunId = Guid.NewGuid();
            var activeValidationRunId = Guid.NewGuid();
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Runs.Add(new CollectionRun
                {
                    Id = abandonedId,
                    StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    Status = "running"
                });
                seed.ValidationRuns.AddRange(
                    new ValidationRun
                    {
                        Id = expiredValidationRunId,
                        LeaseId = Guid.NewGuid(),
                        StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
                        FinishedAt = DateTimeOffset.UtcNow.AddDays(-40).AddMinutes(1),
                        Status = "completed"
                    },
                    new ValidationRun
                    {
                        Id = activeValidationRunId,
                        LeaseId = Guid.NewGuid(),
                        StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
                        Status = "running"
                    });
                seed.Sources.Add(new ProxySource
                {
                    Id = sourceId,
                    Name = "Integration feed",
                    Url = "https://8.8.8.8/feed.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                await seed.SaveChangesAsync();
            }

            var settings = new CollectorOptions
            {
                SourceRetryCount = 0,
                SourceTimeoutSeconds = 30,
                MaxProxiesPerSource = 2,
                MaxCandidatesPerRun = 1
            };
            using (var clients = new TestHttpClientFactory(new StaticFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var completed = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

                Assert.Equal("completed", completed.Status);
                Assert.Equal(1, completed.SourcesProcessed);
                Assert.Equal(1, completed.SourcesSucceeded);
                Assert.Equal(0, completed.SourcesFailed);
                Assert.Equal(1, completed.CandidatesFound);
                Assert.Equal(1, completed.NewProxies);
                Assert.Equal(1, completed.SourcesTruncated);
                Assert.True(completed.CandidateLimitReached);
            }
            DateTimeOffset firstContentFetchedAt;
            await using (var firstContent = await factory.CreateDbContextAsync())
                firstContentFetchedAt = (await firstContent.Sources.AsNoTracking()
                    .SingleAsync(source => source.Id == sourceId)).LastContentFetchedAt!.Value;

            using (var clients = new TestHttpClientFactory(new NotModifiedFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var unchanged = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

                Assert.Equal("completed", unchanged.Status);
                Assert.Equal(1, unchanged.SourcesProcessed);
                Assert.Equal(1, unchanged.SourcesSucceeded);
                Assert.Equal(0, unchanged.SourcesFailed);
                Assert.Equal(0, unchanged.CandidatesFound);
                Assert.Equal(0, unchanged.NewProxies);
                Assert.Equal(1, unchanged.SourcesTruncated);
                Assert.False(unchanged.CandidateLimitReached);
            }
            await using (var unchangedContent = await factory.CreateDbContextAsync())
                Assert.Equal(firstContentFetchedAt, (await unchangedContent.Sources.AsNoTracking()
                    .SingleAsync(source => source.Id == sourceId)).LastContentFetchedAt);

            var staleContentFetchedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await using (var ageContent = await factory.CreateDbContextAsync())
            {
                await ageContent.Sources.Where(source => source.Id == sourceId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(source => source.LastContentFetchedAt, staleContentFetchedAt));
                // Имитируем proxy, уже удалённый retention во время серии 304.
                await ageContent.Proxies.ExecuteDeleteAsync();
            }
            using (var clients = new TestHttpClientFactory(new FullRefreshFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var refreshed = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

                Assert.Equal("completed", refreshed.Status);
                Assert.Equal(1, refreshed.SourcesSucceeded);
                Assert.Equal(1, refreshed.CandidatesFound);
                Assert.Equal(1, refreshed.NewProxies);
                Assert.True(refreshed.CandidateLimitReached);
            }

            var hangingHandler = new HangingFeedHandler();
            using (var clients = new TestHttpClientFactory(hangingHandler))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = collector.CollectAsync(cancellation.Token, forceAllSources: true);
                await hangingHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            }

            await using var verify = await factory.CreateDbContextAsync();
            var runs = await verify.Runs.AsNoTracking().OrderBy(run => run.StartedAt).ToListAsync();
            Assert.Equal(5, runs.Count);
            var abandoned = runs.Single(run => run.Id == abandonedId);
            Assert.Equal("failed", abandoned.Status);
            Assert.NotNull(abandoned.FinishedAt);
            Assert.Contains("прерван", abandoned.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, runs.Count(run => run.Status == "completed" && run.FinishedAt != null));
            var failed = Assert.Single(runs, run => run.Id != abandonedId && run.Status == "failed");
            Assert.NotNull(failed.FinishedAt);
            Assert.Contains("CanceledException", failed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(runs, run => run.Status == "running" || run.FinishedAt == null);
            Assert.False(await verify.ValidationRuns.AnyAsync(run => run.Id == expiredValidationRunId));
            Assert.True(await verify.ValidationRuns.AnyAsync(run => run.Id == activeValidationRunId && run.Status == "running"));
            Assert.Equal(1, await verify.Proxies.CountAsync());
            var source = await verify.Sources.AsNoTracking().SingleAsync(item => item.Id == sourceId);
            Assert.Equal(2, source.LastItemCount);
            Assert.True(source.LastResultTruncated);
            Assert.Equal(0, source.ConsecutiveFailures);
            Assert.Null(source.LastError);
            Assert.NotNull(source.LastSucceededAt);
            Assert.Equal("\"feed-v3\"", source.HttpETag);
            Assert.Equal(StaticFeedHandler.LastModifiedAt, source.HttpLastModifiedAt);
            Assert.True(source.LastContentFetchedAt > staleContentFetchedAt);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ProxyHarborDbContext> options)
        : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => new(options);
        public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("sources", name);
            return _client;
        }

        public void Dispose() => _client.Dispose();
    }

    private sealed class StaticFeedHandler : HttpMessageHandler
    {
        internal static readonly DateTimeOffset LastModifiedAt =
            new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1.1.1.1:80\n8.8.8.8:81\n1.1.1.1:80\n9.9.9.9:82")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"feed-v1\"");
            response.Content.Headers.LastModified = LastModifiedAt;
            return Task.FromResult(response);
        }
    }

    private sealed class NotModifiedFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("\"feed-v1\"", request.Headers.IfNoneMatch.Single().ToString());
            Assert.Equal(StaticFeedHandler.LastModifiedAt, request.Headers.IfModifiedSince);
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"feed-v2\"");
            response.Content.Headers.LastModified = StaticFeedHandler.LastModifiedAt;
            return Task.FromResult(response);
        }
    }

    private sealed class FullRefreshFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1.1.1.1:80\n8.8.8.8:81\n1.1.1.1:80\n9.9.9.9:82")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"feed-v3\"");
            response.Content.Headers.LastModified = StaticFeedHandler.LastModifiedAt;
            return Task.FromResult(response);
        }
    }

    private sealed class HangingFeedHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
