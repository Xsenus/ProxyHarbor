using System.Security.Cryptography;
using ProxyHarbor.Domain;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class VpnSnapshotAdmissionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(200)]
    public void CachedWindowStopsAtQuotaAndLeavesCursorAtFirstUnadmittedIdentity(int quota)
    {
        var source = Source();
        var state = State(source, Feed(201));
        var result = new VpnSnapshotAdmission(quota, 500).Admit(source, state, Tail(state), null, Epoch.AddHours(1));
        Assert.Equal(quota, result.Progress.NextIndex);
        Assert.Equal(Enumerable.Range(1, quota), Items(result).Select(item => item.Port));
        Assert.All(result.Batches, batch => Assert.Equal(Epoch, batch.ObservedAt));
        Assert.Null(result.Progress.FreshBodyHash);
    }

    [Fact]
    public void OnlyIdenticalCompleteBodyCanConfirmTheCachedWindowEpoch()
    {
        var source = Source();
        var state = State(source, Feed(3));
        var fresh = VpnCandidateSnapshotCodec.Encode(Feed(3), VpnProtocol.Vless);
        var confirmedAt = Epoch.AddHours(1);
        var result = new VpnSnapshotAdmission(2, 2).Admit(source, state, Tail(state), fresh, confirmedAt);
        Assert.Equal(2, result.Progress.NextIndex);
        Assert.All(result.Batches, batch => Assert.Equal(confirmedAt, batch.ObservedAt));
        Assert.Equal(fresh.BodyHash, result.Progress.FreshBodyHash);
    }

    [Fact]
    public void FreshAndTailShareQuotaAndDuplicateRetainsFreshUriWithoutConsumingExtraSlot()
    {
        var source = Source();
        var state = State(source, Feed(3));
        var fresh = VpnCandidateSnapshotCodec.Encode("vless://rotated@8.8.8.8:1", VpnProtocol.Vless);
        var result = new VpnSnapshotAdmission(2, 2).Admit(source, state, Tail(state), fresh, Epoch.AddHours(1));
        Assert.Equal(2, result.Progress.NextIndex);
        Assert.Equal(2, Items(result).Length);
        Assert.Contains("rotated", Items(result).Single(x => x.Port == 1).ConnectionUri);
        Assert.Equal(Epoch.AddHours(1), result.Batches.Single(x => x.Candidates.Any(c => c.Port == 1)).ObservedAt);
        Assert.Equal(Epoch, result.Batches.Single(x => x.Candidates.Any(c => c.Port == 2)).ObservedAt);
        Assert.Equal(fresh.BodyHash, result.Progress.FreshBodyHash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void EffectiveGlobalQuotaOneAlternatesFreshAndTail(int sourceQuota)
    {
        var source = Source();
        var state = State(source, Feed(3));
        var fresh = VpnCandidateSnapshotCodec.Encode("vless://fresh@8.8.8.8:9", VpnProtocol.Vless);
        var first = new VpnSnapshotAdmission(sourceQuota, 1).Admit(source, state, Tail(state), fresh, Epoch.AddHours(1));
        Assert.Equal(9, Assert.Single(Items(first)).Port);
        Assert.Equal(0, first.Progress.NextIndex);
        Assert.False(first.Progress.PreferFresh);
        state.PreferFresh = first.Progress.PreferFresh;
        var second = new VpnSnapshotAdmission(sourceQuota, 1).Admit(source, state, Tail(state), fresh, Epoch.AddHours(2));
        Assert.Equal(1, Assert.Single(Items(second)).Port);
        Assert.Equal(1, second.Progress.NextIndex);
        Assert.True(second.Progress.PreferFresh);
        Assert.Null(second.Progress.FreshBodyHash);
    }

    [Fact]
    public void ExhaustedBudgetDoesNotAdvanceCursorLaneHashOrEpoch()
    {
        var source = Source();
        var state = State(source, Feed(3));
        state.PreferFresh = false;
        var admission = new VpnSnapshotAdmission(2, 1);
        var firstSource = Source("first");
        var firstState = State(firstSource, Feed(3));
        admission.Admit(firstSource, firstState, Tail(firstState), null, Epoch.AddHours(1));
        var fresh = VpnCandidateSnapshotCodec.Encode("vless://fresh@8.8.8.8:9", VpnProtocol.Vless);
        var result = admission.Admit(source, state, Tail(state), fresh, Epoch.AddHours(2));
        Assert.Empty(result.Batches);
        Assert.Equal(0, result.Progress.NextIndex);
        Assert.False(result.Progress.PreferFresh);
        Assert.Null(result.Progress.FreshBodyHash);
    }

    [Fact]
    public void ClockRegressionCannotReplaceHigherObservedEpochForDuplicateIdentity()
    {
        var source = Source();
        var state = State(source, Feed(3));
        var fresh = VpnCandidateSnapshotCodec.Encode("vless://new-body@8.8.8.8:1", VpnProtocol.Vless);
        var result = new VpnSnapshotAdmission(2, 2).Admit(source, state, Tail(state), fresh, Epoch.AddMinutes(-1));
        Assert.Contains("original", Items(result).Single(x => x.Port == 1).ConnectionUri);
        Assert.All(result.Batches, batch => Assert.Equal(Epoch, batch.ObservedAt));
    }

    [Fact]
    public void CompletedSnapshotHasNoAdmissionButPreservesVersionGuard()
    {
        var source = Source();
        var state = State(source, Feed(1));
        state.NextIndex = 1;
        state.Payload = [];
        state.PayloadHash = [];
        var result = new VpnSnapshotAdmission(1, 1).Admit(source, state, [], null, Epoch.AddHours(1));
        Assert.Empty(result.Batches);
        Assert.Equal(VpnSourceImportCheckpoint.Capture(state), result.Progress.State);
        Assert.Equal(1, result.Progress.NextIndex);
    }

    [Fact]
    public async Task ConcurrentSourcesCannotOversubscribeSharedBudget()
    {
        var admission = new VpnSnapshotAdmission(5, 13);
        var results = await Task.WhenAll(Enumerable.Range(1, 16).Select(index => Task.Run(() =>
        {
            var source = Source($"source{index}");
            var state = State(source, Feed(20));
            return admission.Admit(source, state, Tail(state), null, Epoch.AddHours(1));
        })));
        Assert.Equal(13, results.Sum(result => Items(result).Length));
        Assert.Equal(13, results.Sum(result => result.Progress.NextIndex));
        Assert.All(results, result => Assert.InRange(Items(result).Length, 0, 5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void NonpositiveQuotasAreRejected(int source, int global) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VpnSnapshotAdmission(source, global));

    private static VpnCandidate[] Items(VpnAdmissionResult result) =>
        result.Batches.SelectMany(batch => batch.Candidates).ToArray();

    private static string Feed(int count) =>
        string.Join('\n', Enumerable.Range(1, count).Select(port => $"vless://original@8.8.8.8:{port}"));

    private static VpnSource Source(string name = "source") => new()
    {
        Name = name,
        Provider = "Test",
        Url = $"https://8.8.8.8/{name}",
        License = "MIT"
    };

    private static VpnSourceImportState State(VpnSource source, string body)
    {
        var snapshot = VpnCandidateSnapshotCodec.Encode(body, VpnProtocol.Vless);
        return new VpnSourceImportState
        {
            VpnSourceId = source.Id,
            SourceUrl = source.Url,
            SourceProtocol = source.DefaultProtocol,
            CreatedAt = Epoch,
            CandidateCount = snapshot.UniqueCount,
            Payload = snapshot.Payload,
            PayloadHash = SHA256.HashData(snapshot.Payload),
            SnapshotBodyHash = snapshot.BodyHash,
            FreshBodyHash = snapshot.BodyHash
        };
    }

    private static List<VpnCandidate> Tail(VpnSourceImportState state)
    {
        var candidates = new List<VpnCandidate>();
        VpnSourceImportStore.ReadWindow(state, 500, candidate => { candidates.Add(candidate); return true; });
        return candidates;
    }
}
