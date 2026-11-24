using System.Text.Json;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;
using static Andril.UnitTests.Hobbies.Xp.HobbiesXpReceiptContractCandidate;
using Xunit;

namespace Andril.UnitTests.Hobbies.Xp;

// Synthetic consistency and consumption tests only. No storage, shared XP engine or real grants.
public sealed class HobbiesXpReceiptRestorationTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid OtherOwner = Guid.NewGuid();
    private static readonly SyntheticScope Scope = new(Owner, ModuleNames.Hobbies, true);
    private static CandidateEvent Event() => new(Owner, ModuleNames.Hobbies, Guid.NewGuid(), Guid.NewGuid(), SyntheticAsset.Xp);
    private static VerificationResult Verified(CandidateEvent input) => new(SyntheticEvidenceOutcome.Verified, input);

    [Theory]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("event")]
    [InlineData("evidence")]
    public void Restore_AlteredReceiptEvidenceIsRejectedWithoutChangingOriginal(string field)
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        var snapshot = candidate.SnapshotForTrustedRestoration();
        var altered = field switch
        {
            "owner" => snapshot[0] with { Owner = OtherOwner },
            "module" => snapshot[0] with { Module = ModuleNames.Learning },
            "event" => snapshot[0] with { EventId = Guid.NewGuid() },
            _ => snapshot[0] with { EvidenceId = Guid.NewGuid() }
        };

        var error = Assert.Throws<InvalidOperationException>(() => HobbiesOnlyCandidate.Restore([input], [altered]));
        Assert.Equal("Invalid synthetic restoration evidence.", error.Message);
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId));
        Assert.Equal(snapshot, candidate.SnapshotForTrustedRestoration());
    }

    [Fact]
    public void Restore_ExactReplayReturnsOriginalMetadataWithoutAnotherAcceptance()
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        var restored = HobbiesOnlyCandidate.Restore([input], candidate.SnapshotForTrustedRestoration());

        var replay = restored.AcceptSyntheticBackendEvent(Scope, input);
        Assert.Equal(original, replay);
        Assert.NotSame(original, replay);
        Assert.Same(replay, restored.AcceptSyntheticBackendEvent(Scope, input));
        Assert.Equal(1, restored.ReceiptCount);
        Assert.Throws<InvalidOperationException>(() => restored.AcceptSyntheticBackendEvent(Scope,
            input with { EvidenceId = Guid.NewGuid() }));
        Assert.Same(replay, restored.FindReceipt(Scope, input.EventId));
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("missing-event")]
    [InlineData("duplicate-receipt")]
    [InlineData("duplicate-event")]
    [InlineData("invalid-asset")]
    public void Restore_IncompleteDuplicateOrInvalidAcceptedEvidenceCannotRestore(string invalid)
    {
        var input = Event();
        var candidate = new HobbiesOnlyCandidate();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        var receipt = candidate.SnapshotForTrustedRestoration()[0];
        CandidateEvent[] events = invalid switch
        {
            "missing-event" => [],
            "duplicate-event" => [input, input],
            "invalid-asset" => [input with { Asset = SyntheticAsset.FlightPoints }],
            _ => [input]
        };
        ReceiptEvidence[] receipts = invalid switch
        {
            "missing-receipt" => [],
            "duplicate-receipt" => [receipt, receipt],
            _ => [receipt]
        };

        if (invalid == "invalid-asset")
            Assert.Throws<ArgumentException>(() => HobbiesOnlyCandidate.Restore(events, receipts));
        else
            Assert.Throws<InvalidOperationException>(() => HobbiesOnlyCandidate.Restore(events, receipts));
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId));
        Assert.Equal(1, candidate.ReceiptCount);
    }

    [Theory]
    [InlineData(SyntheticEvidenceOutcome.Missing)]
    [InlineData(SyntheticEvidenceOutcome.Unavailable)]
    [InlineData(SyntheticEvidenceOutcome.Denied)]
    [InlineData((SyntheticEvidenceOutcome)0)]
    public async Task Verification_UnsuccessfulOrUnknownOutcomeCreatesNoReceipt(SyntheticEvidenceOutcome outcome)
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        // Even matching metadata cannot turn an unsuccessful verifier outcome into acceptance.
        Assert.Null(await candidate.VerifyAndAcceptAsync(Scope, input,
            _ => Task.FromResult(new VerificationResult(outcome, input))));
        Assert.Null(candidate.FindReceipt(Scope, input.EventId));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Verification_MissingCanonicalEvidenceCannotAcceptVerifiedOutcome()
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        Assert.Null(await candidate.VerifyAndAcceptAsync(Scope, input,
            _ => Task.FromResult(new VerificationResult(SyntheticEvidenceOutcome.Verified, null))));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Verification_CancellationBeforeVerificationDoesNotCallDependency()
    {
        var candidate = new HobbiesOnlyCandidate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var input = Event();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => candidate.VerifyAndAcceptAsync(Scope, input, _ =>
        {
            calls++;
            return Task.FromResult(Verified(input));
        }, cancellation.Token));
        Assert.Equal(0, calls);
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Verification_CancellationAfterSuccessfulVerificationPreventsAcceptanceAndAllowsRetry()
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => candidate.VerifyAndAcceptAsync(Scope, input, token =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            cancellation.Cancel();
            return Task.FromResult(Verified(input));
        }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal(0, candidate.ReceiptCount);
        Assert.Null(candidate.FindReceipt(Scope, input.EventId));
        var accepted = await candidate.VerifyAndAcceptAsync(Scope, input, _ => Task.FromResult(Verified(input)));
        Assert.NotNull(accepted);
        Assert.Same(accepted, await candidate.VerifyAndAcceptAsync(Scope, input, _ => Task.FromResult(Verified(input))));
        Assert.Equal(1, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Verification_CancellationDuringDependencyWaitCreatesNoReceipt()
    {
        var candidate = new HobbiesOnlyCandidate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var attempt = candidate.VerifyAndAcceptAsync(Scope, Event(), async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable verification result.");
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("event")]
    [InlineData("evidence")]
    [InlineData("asset")]
    public async Task Verification_CanonicalSourceSubstitutionCannotCreateOrExposeReceipt(string field)
    {
        var candidate = new HobbiesOnlyCandidate();
        var source = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, source);
        var substituted = field switch
        {
            "owner" => source with { Owner = OtherOwner },
            "module" => source with { Module = ModuleNames.Social },
            "event" => source with { EventId = Guid.NewGuid() },
            "evidence" => source with { EvidenceId = Guid.NewGuid() },
            _ => source with { Asset = SyntheticAsset.Coins }
        };
        // For owner substitution, the foreign caller and submitted owner agree; only the
        // separately supplied synthetic source evidence reveals the real owner mismatch.
        var request = field == "owner" ? substituted : source;
        var scope = field == "owner" ? Scope with { Owner = OtherOwner } : Scope;
        var canonical = field == "owner" ? source : substituted;
        var result = await candidate.VerifyAndAcceptAsync(scope, request, _ => Task.FromResult(Verified(canonical)));
        Assert.Null(result);
        Assert.Equal("null", JsonSerializer.Serialize(result));
        Assert.Same(original, candidate.FindReceipt(Scope, source.EventId));
        Assert.Equal(1, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Verification_DependencyExceptionDoesNotCreateReceiptAndRetryCanSucceed()
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        await Assert.ThrowsAsync<IOException>(() => candidate.VerifyAndAcceptAsync(Scope, input,
            _ => Task.FromException<VerificationResult>(new IOException("Synthetic verifier unavailable."))));
        Assert.Equal(0, candidate.ReceiptCount);
        Assert.NotNull(await candidate.VerifyAndAcceptAsync(Scope, input, _ => Task.FromResult(Verified(input))));
        Assert.Equal(1, candidate.ReceiptCount);
    }

    [Fact]
    public void Restored_ForeignAndMissingLookupsHaveIdenticalMetadataFreeResults()
    {
        var candidate = new HobbiesOnlyCandidate();
        var input = Event();
        candidate.AcceptSyntheticBackendEvent(Scope, input);
        var restored = HobbiesOnlyCandidate.Restore([input], candidate.SnapshotForTrustedRestoration());
        var foreign = Scope with { Owner = OtherOwner };
        var hidden = restored.FindReceipt(foreign, input.EventId);
        var missing = restored.FindReceipt(foreign, Guid.NewGuid());
        Assert.Null(hidden);
        Assert.Null(missing);
        Assert.Equal("null", JsonSerializer.Serialize(hidden));
        Assert.Equal(JsonSerializer.Serialize(missing), JsonSerializer.Serialize(hidden));
        var error = Assert.Throws<ArgumentException>(() => restored.AcceptSyntheticBackendEvent(foreign, input));
        Assert.Equal(new ArgumentException("Invalid synthetic Hobbies XP event.", "input").Message, error.Message);
        Assert.DoesNotContain(input.Owner.ToString(), error.ToString());
        Assert.DoesNotContain(input.EvidenceId.ToString(), error.ToString());
        Assert.Equal(1, restored.ReceiptCount);
    }
}
