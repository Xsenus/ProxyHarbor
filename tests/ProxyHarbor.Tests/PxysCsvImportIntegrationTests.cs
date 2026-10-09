using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;
using static ProxyHarbor.Tests.ProxySourceImportStoreIntegrationTests;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class PxysCsvImportIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task OldParserVersionCannotResumeOrAcknowledgeEvenWhenPayloadWasCleared(bool completed)
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddPxysAsync(database);
        var store = new ProxySourceImportStore(database.Factory);
        var old = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source,
            ProxyCandidateSnapshotCodec.Encode("8.8.8.8:1080\n1.1.1.1:1080", ProxyProtocol.Http), default));
        if (completed)
        {
            Assert.True(await store.AcknowledgeCommittedImportAsync(old, 2, DateTimeOffset.UtcNow, default));
            old = Assert.IsType<ProxySourceImportState>(await store.LoadAsync(source, default));
            Assert.Empty(old.Payload);
        }
        // Exercise the actual additive migration against pending and completed legacy rows.
        await using (var db = database.Factory.CreateDbContext())
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var versionIndex = Array.IndexOf(migrations, "20261008191125_AddProxySourceParserVersion");
            Assert.True(versionIndex > 0);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[versionIndex - 1]);
            await migrator.MigrateAsync();
            var migrated = await db.ProxySourceImportStates.AsNoTracking().SingleAsync();
            Assert.Equal(0, migrated.ParserVersion);
            Assert.Equal(old.SnapshotId, migrated.SnapshotId);
            Assert.Equal(old.NextIndex, migrated.NextIndex);
            Assert.Equal(old.Payload, migrated.Payload);
        }
        old.ParserVersion = 0;
        Assert.Null(await new ProxySourceImportStore(database.Factory).LoadAsync(source, default));
        Assert.False(await store.AcknowledgeCommittedImportAsync(ProxySourceImportCheckpoint.Capture(old), completed ? 2 : 1, DateTimeOffset.UtcNow, default, preferFresh: false));
        Assert.Throws<InvalidDataException>(() => ProxySourceImportStore.ReadWindow(old, 1, _ => true));
        var replacement = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(source,
            ProxyCandidateSnapshotCodec.Encode(PxysCsvFeedAdapter.Extract(PxysCsvFeedAdapterTests.Header + PxysCsvFeedAdapterTests.Row), ProxyProtocol.Http), default));
        Assert.NotEqual(old.SnapshotId, replacement.SnapshotId);
        Assert.Equal(1, replacement.ParserVersion);
        Assert.Equal(0, replacement.NextIndex);
        Assert.False(await store.IsCurrentOrDiscardAsync(old, default));
        Assert.NotNull(await store.LoadAsync(source, default));
        Assert.Equal(ProxyProtocol.Socks5, Assert.Single(ReadAll(replacement)).Protocol);
        Assert.False(await store.AcknowledgeCommittedImportAsync(ProxySourceImportCheckpoint.Capture(old), completed ? 2 : 1, DateTimeOffset.UtcNow, default, preferFresh: false));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task NormalCollectionRejectsOldCacheRespectsBackoffAndRefetchesWithoutValidators()
    {
        await using var database = await SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var source = await AddPxysAsync(database);
        var store = new ProxySourceImportStore(database.Factory);
        await using (var db = database.Factory.CreateDbContext())
        {
            var past = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds());
            source.LastFetchedAt = source.LastSucceededAt = source.LastContentFetchedAt = past;
            source.LastItemCount = 2;
            source.HttpETag = "\"legacy-csv\"";
            source.HttpLastModifiedAt = past;
            source.NextFetchAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
            db.Sources.Update(source);
            await db.SaveChangesAsync();
        }
        _ = await store.BeginAsync(source, ProxyCandidateSnapshotCodec.Encode("8.8.8.8:1080\n1.1.1.1:1080", ProxyProtocol.Http), default);
        await using (var db = database.Factory.CreateDbContext())
            await db.ProxySourceImportStates.Where(item => item.ProxySourceId == source.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ParserVersion, 0));
        using var clients = new RecordingClients();
        using var collector = new ProxyCollector(database.Factory, clients,
            Options.Create(new CollectorOptions { SourceRetryCount = 0 }), NullLogger<ProxyCollector>.Instance);
        Assert.Null(await collector.ImportCachedSourcesAsync(default));
        _ = await collector.CollectAsync(default);
        Assert.Equal(0, clients.Requests);
        await using (var db = database.Factory.CreateDbContext())
        {
            var persisted = await db.Sources.SingleAsync(item => item.Id == source.Id);
            Assert.Equal(source.NextFetchAt, persisted.NextFetchAt);
            Assert.Equal(source.LastSucceededAt, persisted.LastSucceededAt);
            Assert.Equal(source.HttpETag, persisted.HttpETag);
            await db.Sources.Where(item => item.Id == source.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, (DateTimeOffset?)null));
            Assert.Empty(await db.ProxySourceImportStates.ToArrayAsync());
        }
        // A valid unrelated pending snapshot retains its identity through version-aware cleanup.
        var unrelated = await database.AddSourceAsync("unaffected-pending-tail");
        var untouched = Assert.IsType<ProxySourceImportState>(await store.BeginAsync(unrelated,
            ProxyCandidateSnapshotCodec.Encode("9.9.9.9:8080", ProxyProtocol.Http), default));
        Assert.Equal(0, untouched.ParserVersion);
        Assert.NotNull(await store.LoadAsync(unrelated, default));
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.Where(item => item.Id == unrelated.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextFetchAt, DateTimeOffset.UtcNow.AddHours(1)));
        var run = await collector.CollectAsync(default);
        Assert.Equal("completed", run.Status);
        Assert.Equal(1, clients.Requests);
        Assert.False(clients.SentValidators);
        await using (var db = database.Factory.CreateDbContext())
        {
            var proxy = await db.Proxies.SingleAsync(item => item.Host == "8.8.8.8");
            Assert.Equal(ProxyProtocol.Socks5, proxy.Protocol);
            Assert.DoesNotContain(await db.Proxies.ToArrayAsync(), item => item.Host == "8.8.8.8" && item.Protocol == ProxyProtocol.Http);
            var state = await db.ProxySourceImportStates.SingleAsync(item => item.ProxySourceId == source.Id);
            Assert.Equal(1, state.ParserVersion);
            Assert.Equal(state.CandidateCount, state.NextIndex);
            Assert.Equal(untouched.SnapshotId, (await db.ProxySourceImportStates.SingleAsync(item => item.ProxySourceId == unrelated.Id)).SnapshotId);
        }
    }

    private static async Task<ProxySource> AddPxysAsync(SnapshotDatabase database)
    {
        var source = await database.AddSourceAsync("pxys-csv");
        source.Url = PxysCsvFeedAdapter.Url;
        await using var db = database.Factory.CreateDbContext();
        db.Sources.Update(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static List<(string Host, int Port, ProxyProtocol Protocol)> ReadAll(ProxySourceImportState state)
    {
        var endpoints = new List<(string Host, int Port, ProxyProtocol Protocol)>();
        ProxySourceImportStore.ReadWindow(state, int.MaxValue, key => { endpoints.Add(key.ToEndpoint()); return true; });
        return endpoints;
    }

    private sealed class RecordingClients : HttpMessageHandler, IHttpClientFactory
    {
        internal int Requests { get; private set; }
        internal bool SentValidators { get; private set; }
        private readonly HttpClient _client;

        internal RecordingClients() => _client = new HttpClient(this, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            SentValidators |= request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince is not null;
            Assert.Equal(PxysCsvFeedAdapter.Url, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(PxysCsvFeedAdapterTests.Header + PxysCsvFeedAdapterTests.Row) });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
