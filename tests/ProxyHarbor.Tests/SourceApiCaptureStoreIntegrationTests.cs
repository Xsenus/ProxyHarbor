using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

[Collection(PostgresIntegrationGroup.Name)]
public sealed class SourceApiCaptureStoreIntegrationTests
{
    private static readonly string[] ExpectedCaptureConstraints = ["CK_SourceApiCaptureStates_Owner", "CK_SourceApiCaptureStates_Payload"];
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CommittedPagesSurvive429RestartAndRemainUntilAdmission()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var owner = await AddOwnerAsync(database, false);
        var store = new SourceApiCaptureStore(database.Factory);
        SourceApiCaptureCheckpoint? checkpoint = null;
        var calls = 0;
        await Assert.ThrowsAsync<SourceRateLimitException>(() => FreeProxyDbPageCapture.AdvanceAsync(new(), owner.Url,
            owner.MaximumBytes, 4, (_, _) =>
            {
                if (++calls == 2) throw new SourceRateLimitException(DateTimeOffset.UtcNow.AddHours(1), HttpStatusCode.TooManyRequests);
                return Task.FromResult(new SourceFetchResult(Page(2, 2), false, null, null));
            }, async (capture, token) => checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(
                await store.SaveAsync(owner, checkpoint, capture, token)), CancellationToken.None));
        store = new SourceApiCaptureStore(database.Factory);
        checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(await store.LoadAsync(owner, CancellationToken.None));
        var firstObservation = checkpoint.Capture.Pages[0].CapturedAt;
        Assert.Equal(2, checkpoint.Capture.Inspect(owner.MaximumBytes).NextPage);
        var requested = new List<int>();
        var result = await FreeProxyDbPageCapture.AdvanceAsync(checkpoint.Capture, owner.Url, owner.MaximumBytes, 4,
            (url, _) =>
            {
                var page = int.Parse(new Uri(url).Query.Split('&').Single(item => item.StartsWith("page_index=", StringComparison.Ordinal))[11..], System.Globalization.CultureInfo.InvariantCulture);
                requested.Add(page);
                return Task.FromResult(new SourceFetchResult(page == 2 ? Page(2, 1) : Page(3, 3, 2), false, null, null));
            }, async (capture, token) => checkpoint = Assert.IsType<SourceApiCaptureCheckpoint>(
                await store.SaveAsync(owner, checkpoint, capture, token)), CancellationToken.None);
        Assert.Equal([2, 1], requested);
        Assert.NotNull(result.Content);
        Assert.Equal(firstObservation, result.ObservedAt);
        var complete = Assert.IsType<SourceApiCaptureCheckpoint>(await store.LoadAsync(owner, CancellationToken.None));
        Assert.True(complete.Capture.Inspect(owner.MaximumBytes).Complete);
        await using (var db = database.Factory.CreateDbContext())
        {
            var state = await db.SourceApiCaptureStates.SingleAsync();
            Assert.True(state.Complete);
            Assert.Equal(state.Payload.Length, state.StoredBytes);
            var source = await db.Sources.SingleAsync();
            Assert.Null(source.LastFetchedAt);
            Assert.Null(source.LastSucceededAt);
        }
        Assert.False(await store.DiscardAsync(complete with { Version = Guid.NewGuid() }, CancellationToken.None));
        Assert.True(await store.DiscardAsync(complete, CancellationToken.None));
        Assert.Null(await store.LoadAsync(owner, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentCheckpointsRejectStaleVersionsAndConfigurationChanges()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var owner = await AddOwnerAsync(database, true);
        var store = new SourceApiCaptureStore(database.Factory);
        var first = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, First(), CancellationToken.None));
        var next = first.Capture.Append(new(2, false, DateTimeOffset.UtcNow, Page(3, 2)), owner.MaximumBytes);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.SaveAsync(owner, first, next, CancellationToken.None)));
        var winner = Assert.Single(results.OfType<SourceApiCaptureCheckpoint>());
        Assert.NotEqual(first.Version, winner.Version);
        Assert.False(await store.DiscardAsync(first, CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            await db.VpnSources.Where(source => source.Id == owner.SourceId).ExecuteUpdateAsync(setters =>
                setters.SetProperty(source => source.DefaultProtocol, VpnProtocol.Trojan));
        var third = next.Append(new(3, false, DateTimeOffset.UtcNow, Page(3, 1)), owner.MaximumBytes);
        Assert.Null(await store.SaveAsync(owner, winner, third, CancellationToken.None));
        Assert.Null(await store.LoadAsync(owner, CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            Assert.Empty(await db.SourceApiCaptureStates.ToArrayAsync());
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SharedStorageBudgetIsAtomicAcrossProxyAndVpnAndDeletionCascades()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var owners = new[] { await AddOwnerAsync(database, false), await AddOwnerAsync(database, true) };
        var capture = First();
        var bytes = FreeProxyDbPageCaptureCodec.Encode(capture, owners[0].MaximumBytes).Length;
        var store = new SourceApiCaptureStore(database.Factory, bytes);
        var results = await Task.WhenAll(owners.Select(async owner =>
        {
            try { return await store.SaveAsync(owner, null, capture, CancellationToken.None); }
            catch (InvalidDataException error) { Assert.Contains("заполнено", error.Message, StringComparison.Ordinal); return null; }
        }));
        var winner = Assert.Single(results.OfType<SourceApiCaptureCheckpoint>());
        await using (var db = database.Factory.CreateDbContext())
        {
            Assert.Equal(bytes, await db.SourceApiCaptureStates.SumAsync(state => state.StoredBytes));
            if (winner.Owner.Vpn) await db.VpnSources.Where(source => source.Id == winner.Owner.SourceId).ExecuteDeleteAsync();
            else await db.Sources.Where(source => source.Id == winner.Owner.SourceId).ExecuteDeleteAsync();
            Assert.Empty(await db.SourceApiCaptureStates.ToArrayAsync());
        }
        Assert.NotNull(await store.SaveAsync(owners.Single(owner => owner != winner.Owner), null, capture, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CorruptChecksumAndInvalidCompletedFlagRecoverWithoutDeletingNewGeneration()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var owner = await AddOwnerAsync(database, false);
        var store = new SourceApiCaptureStore(database.Factory);
        var old = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, First(), CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            await db.SourceApiCaptureStates.ExecuteUpdateAsync(setters => setters.SetProperty(state => state.PayloadHash, new byte[32]));
        Assert.Null(await store.LoadAsync(owner, CancellationToken.None));
        var current = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(owner, null, First(), CancellationToken.None));
        Assert.False(await store.DiscardAsync(old, CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(owner, CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            await db.SourceApiCaptureStates.ExecuteUpdateAsync(setters => setters.SetProperty(state => state.Complete, true));
        Assert.Null(await store.LoadAsync(owner, CancellationToken.None));
        Assert.False(await store.DiscardAsync(current, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DistributedOriginLeaseAndDurableMaxDeadlineApplyAcrossWorkers()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var first = new SourceApiOriginGate(database.Factory);
        var second = new SourceApiOriginGate(database.Factory);
        Assert.Null(await first.ReadDeadlineAsync(CancellationToken.None));
        var at = DateTimeOffset.UtcNow;
        await using (var lease = Assert.IsType<PostgresAdvisoryLock>(await first.TryAcquireAsync(CancellationToken.None)))
        {
            Assert.Null(await second.TryAcquireAsync(CancellationToken.None));
            await first.ReserveRequestAsync(at, CancellationToken.None);
            Assert.Equal(at.AddSeconds(10).UtcDateTime.Ticks / 10, (await second.ReadDeadlineAsync(CancellationToken.None))!.Value.UtcDateTime.Ticks / 10);
            await first.ExtendDeadlineAsync(at.AddHours(1), CancellationToken.None);
            await first.ReserveRequestAsync(at.AddMinutes(1), CancellationToken.None);
        }
        await using var nextLease = Assert.IsType<PostgresAdvisoryLock>(await second.TryAcquireAsync(CancellationToken.None));
        await Task.WhenAll(first.ExtendDeadlineAsync(at.AddHours(2), CancellationToken.None),
            second.ExtendDeadlineAsync(at.AddSeconds(30), CancellationToken.None));
        Assert.Equal(at.AddHours(2).UtcDateTime.Ticks / 10, (await new SourceApiOriginGate(database.Factory).ReadDeadlineAsync(CancellationToken.None))!.Value.UtcDateTime.Ticks / 10);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CaptureOwnerAndPayloadConstraintsAreEnforcedByPostgres()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var proxy = await AddOwnerAsync(database, false);
        var vpn = await AddOwnerAsync(database, true);
        var payload = FreeProxyDbPageCaptureCodec.Encode(First(), proxy.MaximumBytes);
        foreach (var invalid in new[]
        {
            State(null, null, payload, SHA256.HashData(payload)),
            State(proxy.SourceId, vpn.SourceId, payload, SHA256.HashData(payload)),
            State(proxy.SourceId, null, [], SHA256.HashData(payload)),
            State(proxy.SourceId, null, payload, new byte[31]),
            State(proxy.SourceId, null, payload, SHA256.HashData(payload), 4),
            State(null, vpn.SourceId, payload, SHA256.HashData(payload), 8),
        })
        {
            await using var db = database.Factory.CreateDbContext();
            db.SourceApiCaptureStates.Add(invalid);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<Npgsql.PostgresException>(error.InnerException);
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Contains(postgres.ConstraintName, ExpectedCaptureConstraints);
        }
    }

    private static SourceApiCaptureState State(Guid? proxy, Guid? vpn, byte[] payload, byte[] hash, int protocol = 0) =>
        new()
        {
            ProxySourceId = proxy,
            VpnSourceId = vpn,
            SourceUrl = FreeProxyDbFeedFetcher.Url,
            SourceProtocol = protocol,
            Payload = payload,
            PayloadHash = hash,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CleanupFreesDisabledAndReconfiguredCapturesWithoutTouchingActiveSource()
    {
        await using var database = await ProxySourceImportStoreIntegrationTests.SnapshotDatabase.CreateAsync();
        if (database is null) return;
        var proxy = await AddOwnerAsync(database, false);
        var vpn = await AddOwnerAsync(database, true);
        var store = new SourceApiCaptureStore(database.Factory);
        var old = Assert.IsType<SourceApiCaptureCheckpoint>(await store.SaveAsync(proxy, null, First(), CancellationToken.None));
        Assert.NotNull(await store.SaveAsync(vpn, null, First(), CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
            await db.Sources.ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Enabled, false));
        await store.CleanupAsync(CancellationToken.None);
        Assert.False(await store.DiscardAsync(old, CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(vpn, CancellationToken.None));
        await using (var db = database.Factory.CreateDbContext())
        {
            Assert.Single(await db.SourceApiCaptureStates.ToArrayAsync());
            await db.VpnSources.ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Url, "https://example.com/changed"));
        }
        await store.CleanupAsync(CancellationToken.None);
        await using (var db = database.Factory.CreateDbContext())
            Assert.Empty(await db.SourceApiCaptureStates.ToArrayAsync());
        Assert.Null(await store.SaveAsync(proxy, null, First(), CancellationToken.None));
        Assert.Null(await store.SaveAsync(vpn, null, First(), CancellationToken.None));
    }

    private static FreeProxyDbPageCapture First() => new FreeProxyDbPageCapture().Append(
        new(1, false, DateTimeOffset.UtcNow, Page(3, 3)), 100_000);
    private static string Page(int total, params int[] ids) => JsonSerializer.Serialize(new
    {
        status = 1,
        data = new { total_count = total, data = ids.Select(id => new { id, protocol = "http", ip = "8.8.8.8", port = 80 }) }
    });
    private static async Task<SourceApiCaptureOwner> AddOwnerAsync(
        ProxySourceImportStoreIntegrationTests.SnapshotDatabase database, bool vpn)
    {
        await using var db = database.Factory.CreateDbContext();
        if (vpn)
        {
            var source = new VpnSource { Name = "API VPN", Provider = "Test fixture", License = "Test fixture", Url = FreeProxyDbPageCapture.VpnUrl, DefaultProtocol = VpnProtocol.Vless };
            db.VpnSources.Add(source);
            await db.SaveChangesAsync();
            return SourceApiCaptureOwner.From(source);
        }
        var proxy = new ProxySource { Name = "API HTTP", Url = FreeProxyDbFeedFetcher.Url };
        db.Sources.Add(proxy);
        await db.SaveChangesAsync();
        return SourceApiCaptureOwner.From(proxy);
    }
}
