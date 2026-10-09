using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnSourceImportStoreTests
{
    [Fact]
    public void ReadDoesNotAcknowledgeAdmissionAndAlwaysUsesCanonicalLatestUri()
    {
        var state = Pending();
        VpnCandidate received = default;
        Assert.Equal(new VpnSnapshotWindow(1, 1, true), VpnSourceImportStore.ReadWindow(state, 1, item => { received = item; return true; }));
        Assert.Equal("vless://latest@8.8.8.8:443", received.ConnectionUri);
        Assert.Equal(0, state.NextIndex);
        Assert.Null(state.LastProgressAt);
        Assert.NotEmpty(state.Payload);
        Assert.Equal(new VpnSnapshotWindow(0, 0, false), VpnSourceImportStore.ReadWindow(state, 1, _ => false));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("large-count")]
    [InlineData("negative-cursor")]
    [InlineData("cursor")]
    [InlineData("body-hash")]
    [InlineData("fresh-hash")]
    [InlineData("empty-payload")]
    [InlineData("payload-hash")]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("header-count")]
    [InlineData("complete-payload")]
    [InlineData("profile-count")]
    [InlineData("profile-cursor")]
    [InlineData("profile-header")]
    public void InvalidMetadataOrChecksumCannotAdmitAnyCandidate(string damage)
    {
        var state = Pending();
        switch (damage)
        {
            case "count": state.CandidateCount = 0; break;
            case "large-count": state.CandidateCount = VpnCandidateSnapshotCodec.MaxRecords + 1; break;
            case "negative-cursor": state.NextIndex = -1; break;
            case "cursor": state.NextIndex = 2; break;
            case "body-hash": state.SnapshotBodyHash = []; break;
            case "fresh-hash": state.FreshBodyHash = []; break;
            case "empty-payload": state.Payload = []; break;
            case "payload-hash": state.PayloadHash = []; break;
            case "checksum": state.Payload[^1] ^= 1; break;
            case "version": state.Payload[0] ^= 1; state.PayloadHash = SHA256.HashData(state.Payload); break;
            case "header-count": state.CandidateCount = 2; break;
            case "profile-count": state.ProfileRecordCount = -1; break;
            case "profile-cursor": state.ProfileNextIndex = state.ProfileRecordCount + 1; break;
            case "profile-header": state.ProfileRecordCount++; break;
            case "complete-payload": state.NextIndex = 1; state.ProfileNextIndex = state.ProfileRecordCount; break;
        }
        Assert.Throws<InvalidDataException>(() => VpnSourceImportStore.ReadWindow(state, 1, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void CompletedStateRetainsBodyEvidenceButRequiresReleasedPayloadAndValidWindowArguments()
    {
        var state = Pending();
        state.NextIndex = state.CandidateCount;
        state.ProfileNextIndex = state.ProfileRecordCount;
        state.Payload = [];
        state.PayloadHash = [];
        Assert.Equal(new VpnSnapshotWindow(0, 1, true), VpnSourceImportStore.ReadWindow(state, 1, _ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentOutOfRangeException>(() => VpnSourceImportStore.ReadWindow(state, 0, _ => true));
        Assert.Throws<ArgumentNullException>(() => VpnSourceImportStore.ReadWindow(state, 1, null!));
        state.PayloadHash = new byte[32];
        Assert.Throws<InvalidDataException>(() => VpnSourceImportStore.ReadWindow(state, 1, _ => true));
    }

    [Fact]
    public void EndpointCompletionCannotHideOriginalProfilesOrPermitEarlyPayloadRelease()
    {
        var state = Pending();
        state.NextIndex = state.CandidateCount;
        Assert.True(VpnSourceImportStore.ReadWindow(state, 1, _ => throw new InvalidOperationException()).Completed);
        var records = new List<VpnCandidate>();
        Assert.Equal(new VpnSnapshotWindow(1, 1, false), VpnSourceImportStore.ReadProfilesWindow(state, 1, item => { records.Add(item); return true; }));
        Assert.Equal("vless://first@8.8.8.8:443", Assert.Single(records).ConnectionUri);
        Assert.Equal(0, state.ProfileNextIndex);
        state.ProfileNextIndex = 1;
        Assert.True(VpnSourceImportStore.ReadProfilesWindow(state, 1, item => { records.Add(item); return true; }).Completed);
        Assert.Equal("vless://latest@8.8.8.8:443", records[1].ConnectionUri);
        state.Payload = [];
        state.PayloadHash = [];
        Assert.Throws<InvalidDataException>(() => VpnSourceImportStore.ReadProfilesWindow(state, 1, _ => true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(VpnSourceImportStore.MaxStoredBytes + 1)]
    public async Task InvalidStorageBudgetIsRejectedBeforeCreatingDatabaseContext(long budget)
    {
        var source = new VpnSource { Name = "test", Provider = "test", License = "test", Url = "https://example.com/feed", DefaultProtocol = VpnProtocol.Vless };
        var store = new VpnSourceImportStore(new NeverUsedFactory(), budget);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.BeginAsync(source,
            VpnCandidateSnapshotCodec.Encode("vless://id@8.8.8.8:443", VpnProtocol.Vless), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task AcknowledgementWithoutImportTransactionCannotOpenConnectionOrAdvanceCursor()
    {
        await using var db = new ProxyHarborDbContext(new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused").Options);
        var state = Pending();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VpnSourceImportStore.AcknowledgeAsync(db,
            VpnSourceImportCheckpoint.Capture(state), 1, DateTimeOffset.UtcNow, default));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.Equal(0, state.NextIndex);
    }

    private static VpnSourceImportState Pending()
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode("vless://first@8.8.8.8:443\nvless://latest@8.8.8.8:443", VpnProtocol.Vless);
        return new VpnSourceImportState
        {
            VpnSourceId = Guid.NewGuid(),
            SourceUrl = "https://example.com/feed",
            SourceProtocol = VpnProtocol.Vless,
            CreatedAt = DateTimeOffset.UtcNow,
            CandidateCount = snapshot.UniqueCount,
            ProfileRecordCount = snapshot.RecordCount,
            Payload = snapshot.Payload,
            PayloadHash = SHA256.HashData(snapshot.Payload),
            SnapshotBodyHash = snapshot.BodyHash,
            FreshBodyHash = snapshot.BodyHash
        };
    }

    private sealed class NeverUsedFactory : IDbContextFactory<ProxyHarborDbContext>
    {
        public ProxyHarborDbContext CreateDbContext() => throw new InvalidOperationException("Database must not be opened");
    }
}
