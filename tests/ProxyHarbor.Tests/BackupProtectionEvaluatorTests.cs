using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class BackupProtectionEvaluatorTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static TheoryData<int, int, int, bool, bool, BackupProtectionState> StateCases => new()
    {
        { 2, 1, 2, false, false, BackupProtectionState.Protected },
        { 1, 1, 2, false, false, BackupProtectionState.Degraded },
        { 0, 1, 1, true, true, BackupProtectionState.Pending },
        { 0, 1, 1, true, false, BackupProtectionState.Unavailable },
        { 0, 1, 1, false, true, BackupProtectionState.Unavailable }
    };

    [Theory]
    [MemberData(nameof(StateCases))]
    public void AppliesRequiredDesiredAndDurabilityContract(
        int verifiedCopies,
        int required,
        int desired,
        bool hasStaging,
        bool hasJobs,
        BackupProtectionState expected)
    {
        var candidates = Enumerable.Range(0, verifiedCopies)
            .Select(index => Candidate(failureDomain: $"domain-{index}"))
            .ToArray();

        var result = BackupProtectionEvaluator.Evaluate(Request(
            candidates,
            required,
            desired,
            hasStaging,
            hasJobs));

        Assert.Equal(expected, result.State);
        Assert.Equal(verifiedCopies, result.VerifiedIndependentCopies);
        Assert.Equal(Math.Max(0, required - verifiedCopies), result.RequiredCopyDebt);
        Assert.Equal(Math.Max(0, desired - verifiedCopies), result.DesiredCopyDebt);
    }

    [Fact]
    public void OnePhysicalCopyOrDestinationCannotCountTwice()
    {
        var original = Candidate(failureDomain: "domain-a");
        var duplicateCopy = original with
        {
            DestinationId = Guid.NewGuid(),
            FailureDomain = "domain-b"
        };
        var duplicateDestination = Candidate(
            destinationId: original.DestinationId,
            failureDomain: "domain-c");

        var result = BackupProtectionEvaluator.Evaluate(Request(
            [original, duplicateCopy, duplicateDestination],
            required: 2,
            desired: 2));

        Assert.Equal(BackupProtectionState.Unavailable, result.State);
        Assert.Equal(1, result.VerifiedPhysicalCopies);
        Assert.Equal(1, result.VerifiedIndependentCopies);
    }

    [Fact]
    public void DestinationsInSameFailureDomainCountOnce()
    {
        var result = BackupProtectionEvaluator.Evaluate(Request(
            [Candidate(failureDomain: "shared"), Candidate(failureDomain: "SHARED")],
            required: 2,
            desired: 2));

        Assert.Equal(1, result.VerifiedIndependentCopies);
        Assert.Equal(2, result.VerifiedPhysicalCopies);
        Assert.Equal(BackupProtectionState.Unavailable, result.State);
    }

    [Fact]
    public void TelegramCapabilityMismatchCannotSatisfyProtection()
    {
        var telegramCopy = Candidate(failureDomain: "telegram") with
        {
            SupportsIndependentVerification = false
        };

        var result = BackupProtectionEvaluator.Evaluate(Request(
            [telegramCopy],
            required: 1,
            desired: 1));

        Assert.Equal(BackupProtectionState.Unavailable, result.State);
        Assert.Equal(0, result.VerifiedIndependentCopies);
    }

    public static TheoryData<BackupProtectionCandidate> RejectedCandidates => new()
    {
        Candidate() with { IsExternal = false },
        Candidate() with { RouteEligible = false },
        Candidate() with { SupportsIndependentVerification = false },
        Candidate() with { State = "verifying" },
        Candidate() with { VerifiedAt = null },
        Candidate() with { NativeLocator = null },
        Candidate() with { PolicyVersion = 1 },
        Candidate() with { ContentSha256 = new string('b', 64) },
        Candidate() with { SizeBytes = 2_049 },
        Candidate() with { FailureDomain = "" }
    };

    [Theory]
    [MemberData(nameof(RejectedCandidates))]
    public void ExcludesCopiesWithoutExactIndependentEvidence(BackupProtectionCandidate candidate)
    {
        var result = BackupProtectionEvaluator.Evaluate(Request([candidate]));

        Assert.Equal(BackupProtectionState.Unavailable, result.State);
        Assert.Equal(0, result.VerifiedPhysicalCopies);
        Assert.Equal(0, result.VerifiedIndependentCopies);
    }

    [Fact]
    public void DoesNotDowngradeRunPolicyToCopyPolicy()
    {
        var stale = Candidate() with { PolicyVersion = 1 };

        var result = BackupProtectionEvaluator.Evaluate(Request([stale]));

        Assert.Equal(2, result.RequiredVerifiedCopies);
        Assert.Equal(2, result.RequiredCopyDebt);
        Assert.False(result.RequiredProtectionMet);
    }

    [Fact]
    public void ApiAcknowledgementUsesStableRunIdAndMachineReadableState()
    {
        var evaluation = BackupProtectionEvaluator.Evaluate(Request(
            [Candidate()],
            required: 1,
            desired: 2));

        var response = BackupTriggerResponse.From("backup.phbackup", true, evaluation);

        Assert.Equal(evaluation.BackupRunId, response.BackupRunId);
        Assert.Equal("degraded", response.ProtectionState);
        Assert.True(response.Degraded);
        Assert.Equal(1, response.DesiredCopyDebt);
    }

    [Theory]
    [InlineData(BackupProtectionState.Protected, 200)]
    [InlineData(BackupProtectionState.Degraded, 200)]
    [InlineData(BackupProtectionState.Pending, 202)]
    [InlineData(BackupProtectionState.Unavailable, 503)]
    public void ApiStatusCodesNeverAcknowledgeUnprotectedBackupAsSuccess(
        BackupProtectionState state,
        int expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, BackupTriggerResponse.StatusCodeFor(state));
    }

    private static BackupProtectionEvaluationRequest Request(
        IReadOnlyCollection<BackupProtectionCandidate> candidates,
        int required = 2,
        int desired = 3,
        bool hasStaging = false,
        bool hasJobs = false) => new(
            Guid.NewGuid(),
            Hash,
            2_048,
            new BackupProtectionPolicySnapshot(Guid.NewGuid(), 2, required, desired),
            hasStaging,
            hasJobs,
            candidates);

    private static BackupProtectionCandidate Candidate(
        Guid? destinationId = null,
        string failureDomain = "domain-a") => new(
            Guid.NewGuid(),
            destinationId ?? Guid.NewGuid(),
            failureDomain,
            IsExternal: true,
            RouteEligible: true,
            SupportsIndependentVerification: true,
            State: "verified",
            ContentSha256: Hash,
            SizeBytes: 2_048,
            PolicyVersion: 2,
            VerifiedAt: DateTimeOffset.UtcNow,
            NativeLocator: "opaque-locator");
}
