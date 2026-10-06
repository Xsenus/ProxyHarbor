using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Общий bounded admission-бюджет и дедупликация fresh/tail перед COPY.</summary>
internal sealed class VpnSnapshotAdmission
{
    private readonly object gate = new();
    private readonly int maximumPerSource;
    private readonly int maximumPerRun;
    private int count;

    internal VpnSnapshotAdmission(int maximumPerSource, int maximumPerRun)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPerSource, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPerRun, 1);
        this.maximumPerSource = maximumPerSource;
        this.maximumPerRun = maximumPerRun;
    }

    internal VpnAdmissionResult Admit(VpnSource source, VpnSourceImportState state,
        IReadOnlyList<VpnCandidate> tail, VpnCandidateSnapshot? fresh, DateTimeOffset observedAt)
    {
        var admitted = new Dictionary<(string Host, int Port, VpnProtocol Protocol, string Transport),
            (VpnCandidate Candidate, DateTimeOffset ObservedAt)>();
        var progress = new VpnSourceImportProgress(VpnSourceImportCheckpoint.Capture(state),
            state.NextIndex, null, state.PreferFresh);
        if (state.NextIndex < state.CandidateCount)
        {
            var originalBodyConfirmed = fresh is not null && fresh.BodyHash.AsSpan().SequenceEqual(state.SnapshotBodyHash);
            var freshWindow = new List<VpnCandidate>();
            if (fresh is not null && !originalBodyConfirmed && (maximumPerSource > 1 || state.PreferFresh))
                VpnCandidateSnapshotCodec.ReadWindow(fresh.Payload, 0, Math.Max(1, maximumPerSource / 2),
                    candidate => { freshWindow.Add(candidate); return true; });
            lock (gate)
            {
                var acceptedFresh = 0;
                var available = Math.Min(maximumPerSource, maximumPerRun - count);
                var wantedFresh = available > 0 && (available > 1 || state.PreferFresh)
                    ? Math.Min(freshWindow.Count, Math.Max(1, available / 2)) : 0;
                foreach (var candidate in freshWindow.Take(wantedFresh))
                {
                    if (!Accept(candidate, observedAt)) break;
                    acceptedFresh++;
                }
                var nextIndex = state.NextIndex;
                foreach (var candidate in tail)
                {
                    if (!Accept(candidate, originalBodyConfirmed ? observedAt : state.CreatedAt)) break;
                    nextIndex++;
                }
                var preferFresh = nextIndex > state.NextIndex || acceptedFresh == 0 ? state.PreferFresh : false;
                if (nextIndex > state.NextIndex) preferFresh = true;
                var confirmedHash = originalBodyConfirmed && nextIndex > state.NextIndex ||
                    acceptedFresh > 0 && acceptedFresh == wantedFresh ? fresh?.BodyHash : null;
                progress = new(VpnSourceImportCheckpoint.Capture(state), nextIndex, confirmedHash, preferFresh);

                bool Accept(VpnCandidate candidate, DateTimeOffset observedAt)
                {
                    var key = (candidate.Host, candidate.Port, candidate.Protocol, candidate.Transport);
                    if (admitted.TryGetValue(key, out var existing))
                    {
                        if (observedAt > existing.ObservedAt) admitted[key] = (candidate, observedAt);
                        return true;
                    }
                    if (admitted.Count >= maximumPerSource || count >= maximumPerRun) return false;
                    admitted.Add(key, (candidate, observedAt));
                    count++;
                    return true;
                }
            }
        }
        var batches = admitted.Values.GroupBy(item => item.ObservedAt)
            .Select(group => new VpnImportBatch(source, group.Select(item => item.Candidate).ToArray(), group.Key)).ToArray();
        return new(batches, progress);
    }
}

internal sealed record VpnSourceImportProgress(
    VpnSourceImportCheckpoint State, int NextIndex, byte[]? FreshBodyHash, bool PreferFresh);

internal sealed record VpnAdmissionResult(IReadOnlyList<VpnImportBatch> Batches, VpnSourceImportProgress Progress);
