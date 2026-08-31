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
    public async Task LastSeenRefreshSkipsValidatorLockedProxyAndRetriesItNextCycle()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_collection_proxy_lock_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString, postgres => postgres.EnableRetryOnFailure())
                .Options;
            var factory = new TestDbFactory(dbOptions);
            var proxyId = Guid.NewGuid();
            var oldLastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.Sources.Add(new ProxySource
                {
                    Name = "Locked proxy feed",
                    Url = "https://8.8.8.8/locked.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                seed.Proxies.Add(new ProxyEndpoint
                {
                    Id = proxyId,
                    Host = "1.1.1.1",
                    Port = 80,
                    Protocol = ProxyProtocol.Http,
                    FirstSeenAt = oldLastSeenAt,
                    LastSeenAt = oldLastSeenAt
                });
                await seed.SaveChangesAsync();
            }

            await using var blocker = new NpgsqlConnection(builder.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockProxy = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"Proxies\" WHERE \"Id\" = @id FOR UPDATE", blocker, blockerTransaction))
            {
                lockProxy.Parameters.AddWithValue("id", proxyId);
                Assert.Equal(proxyId, await lockProxy.ExecuteScalarAsync());
            }

            using var clients = new TestHttpClientFactory(new StaticFeedHandler());
            using var collector = new ProxyCollector(
                factory,
                clients,
                Options.Create(new CollectorOptions
                {
                    SourceRetryCount = 0,
                    LastSeenRefreshMinutes = 1
                }),
                NullLogger<ProxyCollector>.Instance);

            var skippedRun = await collector.CollectAsync(CancellationToken.None, forceAllSources: true)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("completed", skippedRun.Status);
            await using (var skippedVerify = await factory.CreateDbContextAsync())
                Assert.Equal(oldLastSeenAt, await skippedVerify.Proxies.Where(x => x.Id == proxyId)
                    .Select(x => x.LastSeenAt).SingleAsync());

            await blockerTransaction.CommitAsync();
            var refreshedRun = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);
            Assert.Equal("completed", refreshedRun.Status);
            await using var refreshedVerify = await factory.CreateDbContextAsync();
            Assert.True(await refreshedVerify.Proxies.Where(x => x.Id == proxyId)
                .Select(x => x.LastSeenAt).SingleAsync() > oldLastSeenAt);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailedFeedRemainsLocalFailureWhenLoggerThrows()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_source_logging_{Guid.NewGuid():N}";
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
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                seed.Sources.Add(new ProxySource
                {
                    Name = "Failing feed",
                    Url = "https://8.8.8.8/failing.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                await seed.SaveChangesAsync();
            }

            using var clients = new TestHttpClientFactory(new FailedFeedHandler());
            using var collector = new ProxyCollector(
                factory,
                clients,
                Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
                new ThrowingLogger<ProxyCollector>());

            var run = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

            Assert.Equal("completed", run.Status);
            Assert.Equal(1, run.SourcesProcessed);
            Assert.Equal(0, run.SourcesSucceeded);
            Assert.Equal(1, run.SourcesFailed);
            Assert.Equal(0, run.CandidatesFound);
            await using var verify = await factory.CreateDbContextAsync();
            var source = await verify.Sources.AsNoTracking().SingleAsync();
            Assert.Equal(1, source.ConsecutiveFailures);
            Assert.NotNull(source.LastError);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task InFlightResultCannotOverwriteAReconfiguredSource()
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
            var dbOptions = new DbContextOptionsBuilder<ProxyHarborDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            var factory = new TestDbFactory(dbOptions);
            await using (var migrationDb = await factory.CreateDbContextAsync())
                await migrationDb.Database.MigrateAsync();

            var sourceId = Guid.NewGuid();
            var previousFetchAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Sources.Add(new ProxySource
                {
                    Id = sourceId,
                    Name = "Mutable integration feed",
                    Url = "https://8.8.8.8/old.txt",
                    DefaultProtocol = ProxyProtocol.Http,
                    LastFetchedAt = previousFetchAt,
                    LastSucceededAt = previousFetchAt,
                    LastContentFetchedAt = previousFetchAt,
                    LastItemCount = 7,
                    HttpETag = "\"old-etag\""
                });
                await seed.SaveChangesAsync();
            }

            var handler = new EndpointChangingFeedHandler(async token =>
            {
                await using var update = await factory.CreateDbContextAsync(token);
                await update.Sources.Where(source => source.Id == sourceId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(source => source.Url, "https://1.1.1.1/new.txt")
                        .SetProperty(source => source.DefaultProtocol, ProxyProtocol.Socks5)
                        .SetProperty(source => source.LastFetchedAt, (DateTimeOffset?)null)
                        .SetProperty(source => source.LastSucceededAt, (DateTimeOffset?)null)
                        .SetProperty(source => source.LastContentFetchedAt, (DateTimeOffset?)null)
                        .SetProperty(source => source.LastItemCount, 0)
                        .SetProperty(source => source.HttpETag, (string?)null), token);
            });
            using var clients = new TestHttpClientFactory(handler);
            var validationWakeSignal = new ValidationWakeSignal();
            using var collector = new ProxyCollector(
                factory,
                clients,
                Options.Create(new CollectorOptions
                {
                    SourceRetryCount = 0,
                    MaxProxiesPerSource = 10,
                    MaxCandidatesPerRun = 10
                }),
                NullLogger<ProxyCollector>.Instance,
                validationWakeSignal);

            var run = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

            Assert.Equal("completed", run.Status);
            Assert.Equal(1, run.SourcesSucceeded);
            Assert.Equal(1, run.CandidatesFound);
            await validationWakeSignal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            await using var verify = await factory.CreateDbContextAsync();
            var source = await verify.Sources.AsNoTracking().SingleAsync(item => item.Id == sourceId);
            Assert.Equal("https://1.1.1.1/new.txt", source.Url);
            Assert.Equal(ProxyProtocol.Socks5, source.DefaultProtocol);
            Assert.Null(source.LastFetchedAt);
            Assert.Null(source.LastSucceededAt);
            Assert.Null(source.LastContentFetchedAt);
            Assert.Equal(0, source.LastItemCount);
            Assert.Null(source.HttpETag);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangedRunOwnershipRejectsOtherwiseSuccessfulCollection()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_collection_ownership_{Guid.NewGuid():N}";
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
            {
                await migrationDb.Database.MigrateAsync();
                migrationDb.Sources.Add(new ProxySource
                {
                    Name = "Ownership integration feed",
                    Url = "https://8.8.8.8/ownership.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                await migrationDb.SaveChangesAsync();
            }

            using var clients = new TestHttpClientFactory(new AuditChangingFeedHandler(async token =>
            {
                await using var parallel = await factory.CreateDbContextAsync(token);
                await parallel.Runs
                    .Where(run => run.Status == "running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(run => run.Status, "failed")
                        .SetProperty(run => run.FinishedAt, DateTimeOffset.UtcNow)
                        .SetProperty(run => run.Error, "parallel failure"), token);
            }));
            using var collector = new ProxyCollector(
                factory,
                clients,
                Options.Create(new CollectorOptions
                {
                    SourceRetryCount = 0,
                    MaxProxiesPerSource = 10,
                    MaxCandidatesPerRun = 10
                }),
                NullLogger<ProxyCollector>.Instance);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => collector.CollectAsync(CancellationToken.None, forceAllSources: true));

            Assert.Contains("ownership", exception.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = await factory.CreateDbContextAsync();
            var parallelResult = await verify.Runs.AsNoTracking().SingleAsync();
            Assert.Equal("failed", parallelResult.Status);
            Assert.Equal("parallel failure", parallelResult.Error);
            Assert.Equal(0, parallelResult.SourcesProcessed);
            Assert.Equal(0, parallelResult.CandidatesFound);
            Assert.Equal(1, await verify.Proxies.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RetentionFailureCannotLeaveFalseCompletedCollectionAudit()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PROXYHARBOR_INTEGRATION_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schema = $"proxyharbor_retention_failure_{Guid.NewGuid():N}";
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
            await using (var seed = await factory.CreateDbContextAsync())
            {
                await seed.Database.MigrateAsync();
                var old = DateTimeOffset.UtcNow.AddDays(-5);
                seed.Sources.Add(new ProxySource
                {
                    Name = "Retention failure feed",
                    Url = "https://8.8.8.8/feed.txt",
                    DefaultProtocol = ProxyProtocol.Http
                });
                seed.Proxies.Add(new ProxyEndpoint
                {
                    Host = "4.2.2.5",
                    Port = 8080,
                    Status = ProxyStatus.Pending,
                    FirstSeenAt = old,
                    LastSeenAt = old
                });
                await seed.SaveChangesAsync();
                await seed.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION fail_proxy_retention() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                      RAISE EXCEPTION 'retention failure canary';
                    END;
                    $$;
                    CREATE TRIGGER fail_proxy_retention
                    BEFORE DELETE ON "Proxies"
                    FOR EACH STATEMENT EXECUTE FUNCTION fail_proxy_retention();
                    """);
            }

            using var clients = new TestHttpClientFactory(new StaticFeedHandler());
            using var collector = new ProxyCollector(
                factory,
                clients,
                Options.Create(new CollectorOptions { SourceRetryCount = 0 }),
                NullLogger<ProxyCollector>.Instance);

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => collector.CollectAsync(CancellationToken.None, forceAllSources: true));

            Assert.Contains("retention failure canary", exception.Message, StringComparison.Ordinal);
            await using var verify = await factory.CreateDbContextAsync();
            var audit = await verify.Runs.AsNoTracking().SingleAsync();
            Assert.Equal("failed", audit.Status);
            Assert.NotNull(audit.FinishedAt);
            Assert.Contains("retention failure canary", audit.Error, StringComparison.Ordinal);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
            var activelyLeasedDeadProxyId = Guid.NewGuid();
            var expiredLeaseDeadProxyId = Guid.NewGuid();
            var stalePendingProxyId = Guid.NewGuid();
            var staleAliveProxyId = Guid.NewGuid();
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
                var oldFirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10);
                var oldLastSeenAt = DateTimeOffset.UtcNow.AddDays(-4);
                seed.Proxies.AddRange(
                    new ProxyEndpoint
                    {
                        Id = activelyLeasedDeadProxyId,
                        Host = "4.2.2.1",
                        Port = 8080,
                        Status = ProxyStatus.Dead,
                        FirstSeenAt = oldFirstSeenAt,
                        LastSeenAt = oldLastSeenAt,
                        LastCheckedAt = oldLastSeenAt,
                        FailedChecks = 1,
                        CheckLeaseId = Guid.NewGuid(),
                        CheckLeaseUntil = DateTimeOffset.UtcNow.AddHours(1)
                    },
                    new ProxyEndpoint
                    {
                        Id = expiredLeaseDeadProxyId,
                        Host = "4.2.2.2",
                        Port = 8080,
                        Status = ProxyStatus.Dead,
                        FirstSeenAt = oldFirstSeenAt,
                        LastSeenAt = oldLastSeenAt,
                        LastCheckedAt = oldLastSeenAt,
                        FailedChecks = 1,
                        CheckLeaseId = Guid.NewGuid(),
                        CheckLeaseUntil = DateTimeOffset.UtcNow.AddHours(-1)
                    },
                    new ProxyEndpoint
                    {
                        Id = stalePendingProxyId,
                        Host = "4.2.2.3",
                        Port = 8080,
                        Status = ProxyStatus.Pending,
                        FirstSeenAt = oldFirstSeenAt,
                        LastSeenAt = oldLastSeenAt
                    },
                    new ProxyEndpoint
                    {
                        Id = staleAliveProxyId,
                        Host = "4.2.2.4",
                        Port = 8080,
                        Status = ProxyStatus.Alive,
                        FirstSeenAt = oldFirstSeenAt,
                        LastSeenAt = oldLastSeenAt,
                        LastCheckedAt = oldLastSeenAt,
                        LatencyMs = 250,
                        SuccessfulChecks = 1
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
            await using (var retention = await factory.CreateDbContextAsync())
            {
                Assert.False(await retention.Proxies.AnyAsync(proxy => proxy.Id == stalePendingProxyId));
                Assert.False(await retention.Proxies.AnyAsync(proxy => proxy.Id == expiredLeaseDeadProxyId));
                Assert.True(await retention.Proxies.AnyAsync(proxy => proxy.Id == staleAliveProxyId));
                Assert.True(await retention.Proxies.AnyAsync(proxy => proxy.Id == activelyLeasedDeadProxyId));
            }
            DateTimeOffset firstContentFetchedAt;
            await using (var firstContent = await factory.CreateDbContextAsync())
                firstContentFetchedAt = (await firstContent.Sources.AsNoTracking()
                    .SingleAsync(source => source.Id == sourceId)).LastContentFetchedAt!.Value;

            using (var clients = new TestHttpClientFactory(new NotModifiedFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var unchanged = await collector.CollectAsync(CancellationToken.None, forceAllSources: false);

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

            // Ручной force-run обязан игнорировать ещё свежие validators и повторно
            // скачать/распарсить body: иначе source-аудит способен подтвердить старые counts.
            using (var clients = new TestHttpClientFactory(new FullRefreshFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var forced = await collector.CollectAsync(CancellationToken.None, forceAllSources: true);

                Assert.Equal("completed", forced.Status);
                Assert.Equal(1, forced.SourcesSucceeded);
                Assert.Equal(1, forced.CandidatesFound);
                Assert.Equal(0, forced.NewProxies);
                Assert.True(forced.CandidateLimitReached);
            }

            var staleContentFetchedAt = DateTimeOffset.UtcNow.AddDays(-2);
            await using (var ageContent = await factory.CreateDbContextAsync())
            {
                await ageContent.Sources.Where(source => source.Id == sourceId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(source => source.LastContentFetchedAt, staleContentFetchedAt));
                // Имитируем обычный proxy, уже удалённый retention во время серии 304;
                // отдельно арендованная строка остаётся живым canary retention-защиты.
                await ageContent.Proxies
                    .Where(proxy => proxy.Id != activelyLeasedDeadProxyId)
                    .ExecuteDeleteAsync();
            }
            using (var clients = new TestHttpClientFactory(new FullRefreshFeedHandler()))
            using (var collector = new ProxyCollector(
                factory, clients, Options.Create(settings), NullLogger<ProxyCollector>.Instance))
            {
                var refreshed = await collector.CollectAsync(CancellationToken.None, forceAllSources: false);

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
            Assert.Equal(6, runs.Count);
            var abandoned = runs.Single(run => run.Id == abandonedId);
            Assert.Equal("failed", abandoned.Status);
            Assert.NotNull(abandoned.FinishedAt);
            Assert.Contains("прерван", abandoned.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, runs.Count(run => run.Status == "completed" && run.FinishedAt != null));
            var failed = Assert.Single(runs, run => run.Id != abandonedId && run.Status == "failed");
            Assert.NotNull(failed.FinishedAt);
            Assert.Contains("CanceledException", failed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(runs, run => run.Status == "running" || run.FinishedAt == null);
            Assert.False(await verify.ValidationRuns.AnyAsync(run => run.Id == expiredValidationRunId));
            Assert.True(await verify.ValidationRuns.AnyAsync(run => run.Id == activeValidationRunId && run.Status == "running"));
            Assert.True(await verify.Proxies.AnyAsync(proxy => proxy.Id == activelyLeasedDeadProxyId));
            Assert.False(await verify.Proxies.AnyAsync(proxy => proxy.Id == expiredLeaseDeadProxyId));
            Assert.Equal(2, await verify.Proxies.CountAsync());
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

    private sealed class FailedFeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
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

    private sealed class EndpointChangingFeedHandler(Func<CancellationToken, Task> changeEndpoint)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await changeEndpoint(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("8.8.4.4:8080")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"stale-response\"");
            return response;
        }
    }

    private sealed class AuditChangingFeedHandler(Func<CancellationToken, Task> changeAudit)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await changeAudit(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("8.8.4.4:8080")
            };
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
