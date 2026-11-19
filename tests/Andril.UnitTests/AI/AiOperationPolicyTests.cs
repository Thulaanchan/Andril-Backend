using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiOperationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(Id(1), Id(10 + (int)module), module);
    private static AiOperationAdmissionRequest Request() => new(AiModule.Learning, Id(20), Id(21), "Hello வணக்கம் ආයුබෝවන්");
    private static AiOperationReference Reference() => new(Actor(), Id(20), Id(21));
    private static AiAdmissionEvidence Evidence() => new(Now, DateOnly.FromDateTime(Now.UtcDateTime),
        Enumerable.Range(1, 6).Select(Id).ToArray(), 0, 0, Array.Empty<DateTimeOffset>(), Array.Empty<DateTimeOffset>(), false, true, true);
    private static AiOperationSnapshot State() => new(Reference(), AiOperationStatus.Reserved, AiQuotaState.Reserved,
        AiDispatchState.NotDispatched, Now, DateOnly.FromDateTime(Now.UtcDateTime), Id(30), null);

    [Fact]
    public void Correction_AllValidSourceStatesKeepIdentityAndReturnOnlyValidAccountingStates()
    {
        var validSources = new List<AiOperationSnapshot>();
        foreach (var status in Enum.GetValues<AiOperationStatus>())
        foreach (var quota in Enum.GetValues<AiQuotaState>())
        foreach (var dispatch in Enum.GetValues<AiDispatchState>())
        foreach (var attempt in new Guid?[] { null, Id(50) })
        {
            var state = State() with { Status = status, Quota = quota, Dispatch = dispatch, AttemptId = attempt };
            if (AiOperationPolicy.ValidSnapshot(state)) validSources.Add(state);
        }
        Assert.NotEmpty(validSources);
        foreach (var source in validSources)
        foreach (var signal in Enum.GetValues<AiAccountingEvent>().Where(e => e != AiAccountingEvent.Unknown))
        {
            AiOperationSnapshot result;
            try { result = AiOperationPolicy.ApplyAccounting(source, signal); }
            catch (InvalidOperationException) { continue; } // Explicitly unsupported signals deny.
            Assert.True(AiOperationPolicy.ValidSnapshot(result));
            Assert.Equal(source.Operation, result.Operation);
            Assert.Equal(source.AttemptId, result.AttemptId);
            Assert.Equal(source.QuotaDay, result.QuotaDay);
            if (signal is AiAccountingEvent.UserCancellation or AiAccountingEvent.AmbiguousFailure)
                Assert.Equal(source.Quota == AiQuotaState.Released, result.Quota == AiQuotaState.Released);
            if (source.Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled)
                Assert.Equal(source, result);
        }
    }

    [Fact]
    public void Correction_InvalidPrivateInputUsesContentFreeException()
    {
        var sensitive = new string('x', 501) + "recognizable-private-secret";
        var error = Assert.Throws<ArgumentException>(() => AiOperationPolicy.ValidateRequest(Request() with { UserText = sensitive }));
        Assert.DoesNotContain(sensitive, error.ToString());
        Assert.DoesNotContain("recognizable-private-secret", error.Message);
    }

    [Fact]
    public void Correction_PrivateLegacyRecordsPreserveDataButNotDiagnosticContent()
    {
        const string secret = "recognizable-private-prompt-reply-credential";
        var values = new object[]
        {
            new AiProviderPrompt(AiModule.Learning, secret), new AiProviderResponse(secret, false),
            new AiChatReply(AiModule.Learning, secret, false),
            Reference(), State(), new AiManualRetryRequest(Reference(), true),
            new AiStoredAiMessage(Id(21), Id(20), AiStoredMessageRole.User, secret, Now),
            new AiStoredConversation(Id(20), Id(1), Id(10), AiModule.Learning, Now, Now, Now.AddDays(14)),
            new AiTokenEvidence("private-model", secret, 100, 100, 100, 100, 100, 500, 500, true, true)
        };
        foreach (var value in values)
        {
            Assert.DoesNotContain(secret, value.ToString());
            Assert.DoesNotContain(Id(1).ToString(), value.ToString());
            Assert.DoesNotContain(Id(21).ToString(), value.ToString());
        }
        Assert.Equal(secret, new AiProviderResponse(secret, false).Text);
        Assert.Contains(secret, System.Text.Json.JsonSerializer.Serialize(new AiChatReply(AiModule.Learning, secret, false)));
    }

    [Theory]
    [InlineData(AiDispatchState.Confirmed)]
    [InlineData(AiDispatchState.Uncertain)]
    public void Correction_ExecutedSnapshotsRequireAttempt(AiDispatchState dispatch)
    {
        var invalid = State() with { Status = AiOperationStatus.PendingReconciliation, Dispatch = dispatch };
        Assert.False(AiOperationPolicy.ValidSnapshot(invalid));
        Assert.Throws<InvalidOperationException>(() => AiOperationPolicy.ApplyAccounting(invalid, AiAccountingEvent.UserCancellation));
    }

    [Fact]
    public void Correction_DispatchPreservesReservedAttempt()
    {
        var before = State() with { AttemptId = Id(50) };
        var after = before with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, Revision = Id(31) };
        Assert.True(AiOperationPolicy.CanTransition(before, after));
        Assert.False(AiOperationPolicy.CanTransition(before, after with { AttemptId = Id(51) }));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("revision", false)]
    [InlineData("attempt", false)]
    [InlineData("operation", false)]
    [InlineData("identity", false)]
    [InlineData("time", false)]
    public void Correction_UncertainConfirmationRequiresExactProtectedEvidence(string defect, bool valid)
    {
        var before = State() with { Status = AiOperationStatus.PendingReconciliation,
            Dispatch = AiDispatchState.Uncertain, AttemptId = Id(50) };
        var after = before with { Dispatch = AiDispatchState.Confirmed, Revision = Id(31) };
        AiConfirmedExecutionEvidence? evidence = new(new(Reference(), Id(50)), before.Revision, Id(60), Now);
        evidence = defect switch
        {
            "missing" => null,
            "revision" => evidence with { ExpectedRevision = Id(99) },
            "attempt" => evidence with { Attempt = evidence.Attempt with { AttemptId = Id(99) } },
            "operation" => evidence with { Attempt = evidence.Attempt with { Operation = Reference() with { OperationId = Id(99) } } },
            "identity" => evidence with { EvidenceId = Guid.Empty },
            "time" => evidence with { ObservedAtUtc = Now.AddSeconds(-1) },
            _ => evidence
        };
        Assert.Equal(valid, AiOperationPolicy.CanTransition(before, after, evidence));
        Assert.Equal(evidence is null, AiOperationPolicy.CanTransition(after, after, evidence));
        if (valid)
        {
            var cancelled = AiOperationPolicy.ApplyAccounting(after, AiAccountingEvent.UserCancellation);
            Assert.True(AiOperationPolicy.ValidSnapshot(cancelled));
            Assert.Equal(AiQuotaState.Committed, cancelled.Quota);
            Assert.False(AiOperationPolicy.CanTransition(cancelled, before));
        }
    }

    [Fact]
    public void MultilingualInput_IsValidAndNotNormalized()
    {
        var request = Request();
        AiOperationPolicy.ValidateRequest(request);
        Assert.Equal("Hello வணக்கம் ආයුබෝවන්", request.UserText);
        Assert.DoesNotContain(request.UserText, request.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("conversation")]
    [InlineData("operation")]
    [InlineData("text-null")]
    [InlineData("blank")]
    [InlineData("module")]
    public void InvalidRequest_Throws(string defect)
    {
        var request = Request();
        request = defect switch
        {
            "null" => null,
            "conversation" => request with { ConversationId = Guid.Empty },
            "operation" => request with { OperationId = Guid.Empty },
            "text-null" => request with { UserText = null! },
            "blank" => request with { UserText = " \t\n" },
            _ => request with { Module = (AiModule)99 }
        };
        Assert.Throws<ArgumentException>(() => AiOperationPolicy.ValidateRequest(request));
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(501, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void TextElementBoundary_IsEnforced(int count, bool emoji)
    {
        var request = Request() with { UserText = string.Concat(Enumerable.Repeat(emoji ? "👩🏽‍💻" : "a", count)) };
        if (count == 500) AiOperationPolicy.ValidateRequest(request);
        else Assert.Throws<ArgumentException>(() => AiOperationPolicy.ValidateRequest(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UndefinedModules_FailClosed(int module)
    {
        Assert.Throws<ArgumentException>(() => AiOperationPolicy.ValidateRequest(Request() with { Module = (AiModule)module }));
        Assert.False(AiOperationPolicy.ValidActor(Actor() with { Module = (AiModule)module }));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void MalformedAllowlist_Denies(string defect)
    {
        var valid = Evidence().AllowlistedAccounts!.ToArray();
        IReadOnlyList<Guid>? list = defect switch
        {
            "missing" => null,
            "short" => valid[..5],
            "long" => valid.Append(Id(7)).ToArray(),
            "duplicate" => new[] { Id(1), Id(1), Id(2), Id(3), Id(4), Id(5) },
            _ => new[] { Guid.Empty, Id(2), Id(3), Id(4), Id(5), Id(6) }
        };
        Assert.False(AiOperationPolicy.ValidAllowlist(list));
        Assert.Equal(AiOperationOutcome.Denied, AiOperationPolicy.DecideAdmission(Actor(), Evidence() with { AllowlistedAccounts = list }));
    }

    [Fact]
    public void AllowlistAndBudget_RequireTrustedPositiveEvidence()
    {
        Assert.Equal(AiOperationOutcome.Admitted, AiOperationPolicy.DecideAdmission(Actor(), Evidence()));
        Assert.Equal(AiOperationOutcome.NotAllowlisted, AiOperationPolicy.DecideAdmission(Actor() with { UserId = Id(99) }, Evidence()));
        Assert.Equal(AiOperationOutcome.BudgetNotVerified, AiOperationPolicy.DecideAdmission(Actor(), Evidence() with { BudgetAdmissionVerified = false }));
        Assert.Equal(AiOperationOutcome.Denied, AiOperationPolicy.DecideAdmission(Actor(), Evidence() with { EligibilityVerified = false }));
        Assert.Equal(AiOperationOutcome.ConversationBusy, AiOperationPolicy.DecideAdmission(Actor(), Evidence() with { ConversationBusy = true }));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(19, 0, true)]
    [InlineData(0, 19, true)]
    [InlineData(10, 9, true)]
    [InlineData(20, 0, false)]
    [InlineData(10, 10, false)]
    [InlineData(-1, 0, false)]
    [InlineData(0, -1, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    public void DailyCapacity_AggregatesWithoutOverflow(int reserved, int charged, bool expected)
    {
        Assert.Equal(expected, AiOperationPolicy.HasDailyCapacity(reserved, charged));
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void DailyQuota_IsSharedAcrossModules(AiModule module)
    {
        Assert.Equal(AiOperationOutcome.DailyQuotaExhausted,
            AiOperationPolicy.DecideAdmission(Actor(module), Evidence() with { Reservations = 7, Charges = 13 }));
    }

    [Theory]
    [InlineData(2, 9, AiOperationOutcome.Admitted)]
    [InlineData(3, 9, AiOperationOutcome.AccountRateLimited)]
    [InlineData(2, 10, AiOperationOutcome.GlobalRateLimited)]
    public void RateLimits_UseIndependentAccountAndGlobalEvidence(int account, int global, AiOperationOutcome expected)
    {
        var evidence = Evidence() with
        {
            AccountAdmissions = Enumerable.Repeat(Now, account).ToArray(),
            GlobalAdmissions = Enumerable.Repeat(Now, global).ToArray()
        };
        Assert.Equal(expected, AiOperationPolicy.DecideAdmission(Actor(), evidence));
    }

    [Theory]
    [InlineData(-600000001, true)]
    [InlineData(-600000000, true)]
    [InlineData(-599999999, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void RollingWindow_IsOpenAtStartClosedAtNow(long ticks, bool capacity)
    {
        Assert.Equal(capacity, AiOperationPolicy.HasRateCapacity(Enumerable.Repeat(Now.AddTicks(ticks), 3).ToArray(), Now, 3));
    }

    [Fact]
    public void MalformedTimeEvidence_FailsClosed()
    {
        Assert.False(AiOperationPolicy.HasRateCapacity(new[] { default(DateTimeOffset) }, Now, 3));
        Assert.False(AiOperationPolicy.HasRateCapacity(new[] { Now.ToOffset(TimeSpan.FromHours(1)) }, Now, 3));
        Assert.False(AiOperationPolicy.HasRateCapacity(null, Now, 3));
        Assert.False(AiOperationPolicy.HasRateCapacity(Array.Empty<DateTimeOffset>(), default, 3));
        Assert.False(AiOperationPolicy.HasRateCapacity(Array.Empty<DateTimeOffset>(), Now, 99));
        Assert.Equal(AiOperationOutcome.Denied, AiOperationPolicy.DecideAdmission(Actor(), Evidence() with { QuotaDay = DateOnly.MinValue }));
    }

    [Theory]
    [InlineData("same", AiOperationOutcome.Existing)]
    [InlineData("text", AiOperationOutcome.IdempotencyConflict)]
    [InlineData("operation", AiOperationOutcome.IdempotencyConflict)]
    [InlineData("conversation", AiOperationOutcome.IdempotencyConflict)]
    [InlineData("account", AiOperationOutcome.IdempotencyConflict)]
    [InlineData("profile", AiOperationOutcome.IdempotencyConflict)]
    [InlineData("module", AiOperationOutcome.IdempotencyConflict)]
    public void Idempotency_RequiresExactScopeAndImmutableInput(string change, AiOperationOutcome expected)
    {
        var original = Reference();
        var incoming = change switch
        {
            "operation" => original with { OperationId = Id(99) },
            "conversation" => original with { ConversationId = Id(99) },
            "account" => original with { Actor = original.Actor with { UserId = Id(99) } },
            "profile" => original with { Actor = original.Actor with { ModuleProfileId = Id(99) } },
            "module" => original with { Actor = Actor(AiModule.Social) },
            _ => original
        };
        var request = change == "text" ? Request() with { UserText = "changed" } : Request();
        Assert.Equal(expected, AiOperationPolicy.CompareRequests(original, Request(), incoming, request));
    }

    [Fact]
    public void Accounting_ReservesCommitsAndReleasesOnlyOnce()
    {
        var reserved = State();
        Assert.True(AiOperationPolicy.ValidSnapshot(reserved));
        var active = reserved with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) };
        var completed = AiOperationPolicy.ApplyAccounting(active, AiAccountingEvent.UsableCompletion);
        Assert.Equal(AiQuotaState.Committed, completed.Quota);
        Assert.Equal(completed, AiOperationPolicy.ApplyAccounting(completed, AiAccountingEvent.UsableCompletion));
        var failed = reserved with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed };
        var released = AiOperationPolicy.ApplyAccounting(failed, AiAccountingEvent.ConfirmedUnrecoverableFailure);
        Assert.Equal(AiQuotaState.Released, released.Quota);
        Assert.Equal(released, AiOperationPolicy.ApplyAccounting(released, AiAccountingEvent.ConfirmedUnrecoverableFailure));
        Assert.Equal(reserved.QuotaDay, completed.QuotaDay);
        Assert.Equal(reserved.QuotaDay, released.QuotaDay);
    }

    [Theory]
    [InlineData(AiDispatchState.NotDispatched, AiAccountingEvent.UserCancellation, AiQuotaState.Reserved)]
    [InlineData(AiDispatchState.Uncertain, AiAccountingEvent.UserCancellation, AiQuotaState.Reserved)]
    [InlineData(AiDispatchState.Confirmed, AiAccountingEvent.UserCancellation, AiQuotaState.Committed)]
    [InlineData(AiDispatchState.Confirmed, AiAccountingEvent.AmbiguousFailure, AiQuotaState.Reserved)]
    public void CancellationAndAmbiguity_DoNotRefund(AiDispatchState dispatch, AiAccountingEvent signal, AiQuotaState quota)
    {
        var state = State() with { Status = AiOperationStatus.PendingReconciliation, Dispatch = dispatch, AttemptId = Id(40) };
        var changed = AiOperationPolicy.ApplyAccounting(state, signal);
        Assert.Equal(quota, changed.Quota);
        Assert.Equal(state.QuotaDay, changed.QuotaDay);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("quota")]
    [InlineData("dispatch")]
    [InlineData("bucket")]
    [InlineData("revision")]
    [InlineData("attempt")]
    public void MalformedSnapshotsAndTransitions_FailClosed(string defect)
    {
        var state = State();
        state = defect switch
        {
            "status" => state with { Status = (AiOperationStatus)99 },
            "quota" => state with { Quota = (AiQuotaState)99 },
            "dispatch" => state with { Dispatch = (AiDispatchState)99 },
            "bucket" => state with { QuotaDay = state.QuotaDay.AddDays(1) },
            "revision" => state with { Revision = Guid.Empty },
            _ => state with { AttemptId = Guid.Empty }
        };
        Assert.False(AiOperationPolicy.ValidSnapshot(state));
        Assert.Throws<InvalidOperationException>(() => AiOperationPolicy.ApplyAccounting(state, AiAccountingEvent.UserCancellation));
    }

    [Fact]
    public void UnsupportedAccountingTransitions_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => AiOperationPolicy.ApplyAccounting(State(), AiAccountingEvent.UsableCompletion));
        Assert.Throws<InvalidOperationException>(() => AiOperationPolicy.ApplyAccounting(State(), AiAccountingEvent.ConfirmedUnrecoverableFailure));
        Assert.Throws<InvalidOperationException>(() => AiOperationPolicy.ApplyAccounting(State(), (AiAccountingEvent)99));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("failed", true)]
    [InlineData("uncertain", false)]
    [InlineData("completed-without-dispatch", false)]
    [InlineData("wrong-bucket", false)]
    [InlineData("same-revision", false)]
    public void StructuralTransitions_RequireStableIdentityBucketAndRevision(string scenario, bool expected)
    {
        var before = State();
        var after = before with { Revision = Id(31) };
        after = scenario switch
        {
            "active" => after with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) },
            "failed" => after with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed },
            "uncertain" => after with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain },
            "completed-without-dispatch" => after with { Status = AiOperationStatus.Completed, Quota = AiQuotaState.Committed, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) },
            "wrong-bucket" => after with { AdmittedAtUtc = Now.AddDays(1), QuotaDay = before.QuotaDay.AddDays(1) },
            _ => before with { Status = AiOperationStatus.PendingReconciliation }
        };
        Assert.Equal(expected, AiOperationPolicy.CanTransition(before, after));
    }

    [Fact]
    public void ReleasedQuota_CannotBeResurrectedByRetryTransition()
    {
        var released = State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed, Quota = AiQuotaState.Released };
        Assert.False(AiOperationPolicy.CanTransition(released, State() with { Revision = Id(31) }));
        Assert.True(AiOperationPolicy.CanTransition(released, released));
    }

    [Theory]
    [InlineData("eligible", AiRetryDecision.Eligible)]
    [InlineData("released", AiRetryDecision.CapacityContractReviewRequired)]
    [InlineData("completed", AiRetryDecision.Blocked)]
    [InlineData("active", AiRetryDecision.Blocked)]
    [InlineData("uncertain", AiRetryDecision.ReconciliationRequired)]
    [InlineData("wrong", AiRetryDecision.Blocked)]
    [InlineData("revoked", AiRetryDecision.Blocked)]
    [InlineData("unreconciled", AiRetryDecision.ReconciliationRequired)]
    [InlineData("implicit", AiRetryDecision.Blocked)]
    [InlineData("prepared", AiRetryDecision.Blocked)]
    public void Retry_IsExplicitReconciledAndCapacitySafe(string scenario, AiRetryDecision expected)
    {
        var failed = State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed };
        var state = scenario switch
        {
            "released" => failed with { Quota = AiQuotaState.Released },
            "completed" => failed with { Status = AiOperationStatus.Completed, Quota = AiQuotaState.Committed, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) },
            "active" => failed with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) },
            "uncertain" => failed with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain },
            "prepared" => State(),
            _ => failed
        };
        var reference = scenario == "wrong" ? Reference() with { OperationId = Id(99) } : Reference();
        Assert.Equal(expected, AiOperationPolicy.CanRetry(reference, state, scenario != "implicit", scenario != "unreconciled", scenario != "revoked"));
    }
}
