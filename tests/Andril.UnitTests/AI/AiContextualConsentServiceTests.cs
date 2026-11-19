using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Recommendations;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Unit coordination simulations; no durable consumption or concurrency claims.</summary>
public sealed class AiContextualConsentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(Id(1), Id(2), module);
    private static AiConsentReference Reference(AiModule module = AiModule.Learning) => new(new(Actor(module), Id(3), Id(4)), new("fixture-purpose", "v1", "current-conversation"), Id(5), Id(6));
    private static AiConsentLifecycle Live(AiModule module = AiModule.Learning) => new(Reference(module).Operation, Id(5), Id(6), Now, true, true, true, true);
    private static AiConsentSnapshot Snapshot(AiConsentState state, AiModule module = AiModule.Learning) => new(Reference(module), new("v1", "Fixture recommendation", "Use relevant current conversation only"), state, Id(7), Now,
        state is AiConsentState.GrantedUnused or AiConsentState.Started ? Now : null, state == AiConsentState.Started ? Now : null, state == AiConsentState.Started ? Id(8) : null);
    private static AiConsentStartRequest Start() => new(Reference(), Id(10), Id(7), Id(8));
    private static AiConsentResult Result(AiConsentOutcome outcome, AiConsentState state) => new(outcome, Snapshot(state), Live(),
        outcome is AiConsentOutcome.Status or AiConsentOutcome.ContinuationVerified ? null : Id(10));

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("repository")]
    public void A_NullDependencies_AreRejected(string name)
    {
        var s = new Setup();
        var error = Assert.Throws<ArgumentNullException>(() => new AiContextualConsentService(name == "authorization" ? null! : s.Auth,
            name == "privacy" ? null! : s.Privacy, name == "repository" ? null! : s.Repo));
        Assert.Equal(name, error.ParamName);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task A_FreshScopedOffer_ForwardsLifecycleAndToken(AiModule module)
    {
        var s = new Setup();
        s.Auth.Actor = Actor(module);
        s.Repo.Lifecycle = Live(module);
        s.Repo.Result = new(AiConsentOutcome.Offered, Snapshot(AiConsentState.AwaitingConsent, module), Live(module), Id(10));
        using var cts = new CancellationTokenSource();
        var request = new AiConsentOfferRequest(Reference(module), Id(10));
        Assert.Equal(s.Repo.Result, await s.Service.ApplyAsync(request, cts.Token));
        Assert.Equal((request, Live(module), cts.Token), Assert.Single(s.Repo.Mutations));
        Assert.Equal((module, cts.Token), Assert.Single(s.Auth.Calls));
        Assert.Empty(s.Gate.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    public async Task A_ForeignOrRevokedActor_IsDenied(string defect)
    {
        var s = new Setup();
        s.Auth.Actor = defect switch { "missing" => null, "account" => Actor() with { UserId = Id(99) }, "profile" => Actor() with { ModuleProfileId = Id(99) }, _ => Actor(AiModule.Social) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.ApplyAsync(Start()));
        Assert.Empty(s.Repo.Mutations);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("module-epoch")]
    [InlineData("eligibility")]
    [InlineData("conversation")]
    [InlineData("inactive")]
    [InlineData("recovery")]
    public async Task A_InvalidLifecycle_CannotReviveOldGrant(string defect)
    {
        var s = new Setup();
        s.Repo.Lifecycle = defect switch
        {
            "session" => Live() with { SessionEpoch = Id(99) },
            "module-epoch" => Live() with { ModuleEpoch = Id(99) },
            "eligibility" => Live() with { EligibilityVerified = false },
            "conversation" => Live() with { ConversationAvailable = false },
            "inactive" => Live() with { SessionActive = false },
            _ => Live() with { RecoveryVerified = false }
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.ApplyAsync(Start()));
        Assert.Empty(s.Repo.Mutations);
    }

    [Theory]
    [InlineData(AiConsentDecision.Allow, AiConsentState.GrantedUnused)]
    [InlineData(AiConsentDecision.NotNow, AiConsentState.Declined)]
    public async Task B_ExplicitDecisionAndDuplicate_ReconcileWithoutNewOperation(AiConsentDecision decision, AiConsentState state)
    {
        var s = new Setup();
        s.Repo.Result = Result(AiConsentOutcome.DecisionRecorded, state);
        var request = new AiConsentDecisionRequest(Reference(), Id(10), Id(7), decision, Id(11));
        Assert.Equal(AiConsentOutcome.DecisionRecorded, (await s.Service.ApplyAsync(request)).Outcome);
        s.Repo.Result = Result(AiConsentOutcome.AlreadyRecorded, state);
        Assert.Equal(AiConsentOutcome.AlreadyRecorded, (await s.Service.ApplyAsync(request)).Outcome);
        Assert.Equal(s.Repo.Mutations[0].Request, s.Repo.Mutations[1].Request);
        Assert.Empty(s.Gate.Calls);
    }

    [Theory]
    [InlineData(AiConsentDecision.Unknown)]
    [InlineData((AiConsentDecision)99)]
    public async Task B_InvalidDecision_DoesNotReachStorage(AiConsentDecision decision)
    {
        var s = new Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.ApplyAsync(new AiConsentDecisionRequest(Reference(), Id(10), Id(7), decision, Id(11))));
        Assert.Empty(s.Auth.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("purpose")]
    [InlineData("operation")]
    [InlineData("module")]
    [InlineData("lifecycle")]
    [InlineData("state")]
    public async Task B_UnverifiedSuccess_FailsClosed(string defect)
    {
        var s = new Setup();
        var result = Result(AiConsentOutcome.Started, AiConsentState.Started);
        var snapshot = result.Snapshot!;
        s.Repo.Result = defect switch
        {
            "unknown" => new(AiConsentOutcome.Unknown),
            "missing" => new(AiConsentOutcome.Started),
            "purpose" => result with { Snapshot = snapshot with { Reference = Reference() with { Purpose = Reference().Purpose with { Version = "v2" } } } },
            "operation" => result with { Snapshot = snapshot with { Reference = Reference() with { Operation = Reference().Operation with { OperationId = Id(99) } } } },
            "module" => result with { Snapshot = Snapshot(AiConsentState.Started, AiModule.Social) },
            "lifecycle" => result with { Lifecycle = null },
            _ => result with { Snapshot = snapshot with { State = AiConsentState.Revoked } }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.ApplyAsync(Start()));
        Assert.Single(s.Repo.Mutations);
    }

    [Fact]
    public async Task B_RefusalSuppressionAndNewUserEvidence_AreRepositoryVerified()
    {
        var s = new Setup();
        s.Repo.Result = new(AiConsentOutcome.Suppressed);
        Assert.Equal(AiConsentOutcome.Suppressed, (await s.Service.ApplyAsync(new AiConsentOfferRequest(Reference(), Id(10)))).Outcome);
        var next = Reference() with { Operation = Reference().Operation with { OperationId = Id(99) } };
        s.Repo.Lifecycle = Live() with { Operation = next.Operation };
        s.Repo.Result = new(AiConsentOutcome.Offered, Snapshot(AiConsentState.AwaitingConsent) with { Reference = next }, s.Repo.Lifecycle, Id(12));
        var request = new AiConsentOfferRequest(next, Id(12), Id(13));
        Assert.Equal(AiConsentOutcome.Offered, (await s.Service.ApplyAsync(request)).Outcome);
        Assert.Equal(request, s.Repo.Mutations[^1].Request);
        // Evidence ID is not consent; production repository must verify actual new user intent.
    }

    [Fact]
    public async Task C_StartAndRepeatedStart_DoNotReturnTwoStartGrants()
    {
        var s = new Setup();
        s.Repo.Result = Result(AiConsentOutcome.Started, AiConsentState.Started);
        Assert.Equal(AiConsentOutcome.Started, (await s.Service.ApplyAsync(Start())).Outcome);
        s.Repo.Result = Result(AiConsentOutcome.AlreadyRecorded, AiConsentState.Started);
        Assert.Equal(AiConsentOutcome.AlreadyRecorded, (await s.Service.ApplyAsync(Start())).Outcome);
        Assert.Equal(2, s.Repo.Mutations.Count);
        Assert.Equal(2, s.Gate.Calls.Count);
        // Unit simulation only; no claim of durable duplicate prevention.
    }

    [Theory]
    [InlineData(AiConsentOutcome.Expired)]
    [InlineData(AiConsentOutcome.Revoked)]
    [InlineData(AiConsentOutcome.Denied)]
    [InlineData(AiConsentOutcome.Conflict)]
    [InlineData(AiConsentOutcome.ReconciliationRequired)]
    public async Task C_StartDenials_AreNotConvertedIntoSuccess(AiConsentOutcome outcome)
    {
        var s = new Setup();
        s.Repo.Result = new(outcome);
        Assert.Equal(outcome, (await s.Service.ApplyAsync(Start())).Outcome);
        Assert.Single(s.Repo.Mutations);
    }

    [Fact]
    public async Task C_PrivacyIsRequiredForStartAndPendingChecks()
    {
        var s = new Setup();
        s.Gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.ApplyAsync(Start()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.CheckPendingAsync(new(Reference(), Id(7), Id(8))));
        Assert.Empty(s.Repo.Mutations);
        Assert.Empty(s.Repo.Pending);
    }

    [Fact]
    public async Task D_WithdrawalAndStatus_DoNotRequireGenerationPrivacyOrBudget()
    {
        var s = new Setup();
        s.Gate.Outcome = AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        s.Repo.Result = Result(AiConsentOutcome.Withdrawn, AiConsentState.Revoked);
        var withdrawal = new AiConsentWithdrawalRequest(Reference(), Id(10));
        Assert.Equal(AiConsentOutcome.Withdrawn, (await s.Service.ApplyAsync(withdrawal)).Outcome);
        s.Repo.Result = Result(AiConsentOutcome.AlreadyRecorded, AiConsentState.Revoked);
        Assert.Equal(AiConsentOutcome.AlreadyRecorded, (await s.Service.ApplyAsync(withdrawal)).Outcome);
        s.Repo.Result = Result(AiConsentOutcome.Status, AiConsentState.Revoked);
        Assert.Equal(AiConsentOutcome.Status, (await s.Service.ReadStatusAsync(Reference())).Outcome);
        Assert.Empty(s.Gate.Calls);
    }

    [Theory]
    [InlineData(AiConsentSystemOutcome.Invalidated)]
    [InlineData(AiConsentSystemOutcome.AlreadyInvalidated)]
    [InlineData(AiConsentSystemOutcome.Denied)]
    public async Task D_SystemRevocation_DoesNotRequireLiveUser(AiConsentSystemOutcome outcome)
    {
        var s = new Setup();
        s.Auth.Actor = null;
        var evidence = new AiConsentSystemInvalidation(Id(20), Reference(), AiConsentRevocationReason.Logout);
        s.Repo.SystemHandler = request => new(request, outcome);
        Assert.Equal(outcome, (await s.Service.InvalidateFromSystemAsync(evidence)).Outcome);
        Assert.Empty(s.Auth.Calls);
        Assert.Single(s.Repo.Systems);
    }

    [Fact]
    public async Task D_WrongSystemEventScope_FailsClosed()
    {
        var s = new Setup();
        var evidence = new AiConsentSystemInvalidation(Id(20), Reference(), AiConsentRevocationReason.AccountDeletion);
        s.Repo.SystemHandler = request => new(request with { EventId = Id(99) }, AiConsentSystemOutcome.Invalidated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.InvalidateFromSystemAsync(evidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D_PendingResultRequiresCurrentRevisionAndLiveEpochs(bool revoked)
    {
        var s = new Setup();
        s.Repo.Result = Result(AiConsentOutcome.ContinuationVerified, AiConsentState.Started);
        var pending = new AiConsentPendingReference(Reference(), Id(7), Id(8));
        Assert.Equal(AiConsentOutcome.ContinuationVerified, (await s.Service.CheckPendingAsync(pending)).Outcome);
        if (revoked)
        {
            s.Repo.Lifecycle = Live() with { SessionEpoch = Id(99) };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.CheckPendingAsync(pending));
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.CheckPendingAsync(pending with { ConsentRevision = Id(99) }));
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("privacy")]
    [InlineData("lifecycle")]
    [InlineData("apply")]
    public async Task E_DependencyFailure_PropagatesWithoutRetry(string stage)
    {
        var s = new Setup();
        var failure = new InvalidOperationException("Synthetic failure");
        if (stage == "auth") s.Auth.Callback = _ => throw failure;
        if (stage == "privacy") s.Gate.Callback = _ => throw failure;
        s.Repo.Callback = (current, _) => { if (current == stage) throw failure; };
        Assert.Same(failure, await Record.ExceptionAsync(() => s.Service.ApplyAsync(Start())));
        Assert.Equal(stage == "apply" ? 1 : 0, s.Repo.Mutations.Count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("lifecycle")]
    [InlineData("apply")]
    public async Task E_Cancellation_DoesNotImplyRollbackOrRetry(string stage)
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        if (stage == "before") cts.Cancel();
        s.Repo.Callback = (current, token) => { Assert.Equal(cts.Token, token); if (current == stage) cts.Cancel(); };
        s.Repo.Result = Result(AiConsentOutcome.Started, AiConsentState.Started);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Service.ApplyAsync(Start(), cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(stage == "apply" ? 1 : 0, s.Repo.Mutations.Count);
    }

    [Fact]
    public void F_NoticeDiagnostics_DoNotExposeDescriptions()
    {
        Assert.DoesNotContain("Fixture recommendation", Snapshot(AiConsentState.AwaitingConsent).ToString());
        // No audit/history/provider/tool/recommendation access API is introduced; production
        // cleanup and ordinary chat availability require separate integration verification.
    }

    [Theory]
    [InlineData("request")]
    [InlineData("expired-start")]
    [InlineData("session")]
    [InlineData("attempt")]
    public async Task C_StaleConsumptionEvidence_IsRejected(string defect)
    {
        var s = new Setup();
        var result = Result(AiConsentOutcome.Started, AiConsentState.Started);
        s.Repo.Result = defect switch
        {
            "request" => result with { RequestId = Id(99) },
            "expired-start" => result with { Snapshot = result.Snapshot! with { StartedAtUtc = Now.AddMinutes(5) }, Lifecycle = Live() with { NowUtc = Now.AddMinutes(5) } },
            "session" => result with { Lifecycle = Live() with { SessionEpoch = Id(99) } },
            _ => result with { Snapshot = result.Snapshot! with { AttemptId = Id(99) } }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.ApplyAsync(Start()));
        Assert.Single(s.Repo.Mutations);
    }

    [Fact]
    public async Task C_PostCommitTimeIsValidated_WithoutUsingPreflightAsCommitTime()
    {
        var s = new Setup();
        s.Repo.Result = Result(AiConsentOutcome.Started, AiConsentState.Started) with
        {
            Snapshot = Snapshot(AiConsentState.Started) with { StartedAtUtc = Now.AddSeconds(2) },
            Lifecycle = Live() with { NowUtc = Now.AddSeconds(3) }
        };
        Assert.Equal(AiConsentOutcome.Started, (await s.Service.ApplyAsync(Start())).Outcome);
    }

    [Fact]
    public async Task D_CompletedWithdrawal_RemainsPendingPolicy()
    {
        var s = new Setup();
        s.Repo.Result = new(AiConsentOutcome.CompletedWithdrawalPolicyPending);
        Assert.Equal(AiConsentOutcome.CompletedWithdrawalPolicyPending,
            (await s.Service.ApplyAsync(new AiConsentWithdrawalRequest(Reference(), Id(10)))).Outcome);
        Assert.Empty(s.Gate.Calls);
    }

    [Fact]
    public async Task D_MissingSystemAuthority_PropagatesWithoutUserLogin()
    {
        var s = new Setup();
        s.Auth.Actor = null;
        var denial = new UnauthorizedAccessException("Synthetic internal event authority denial");
        s.Repo.Callback = (stage, _) => { if (stage == "system") throw denial; };
        var evidence = new AiConsentSystemInvalidation(Id(20), Reference(), AiConsentRevocationReason.Logout);
        Assert.Same(denial, await Record.ExceptionAsync(() => s.Service.InvalidateFromSystemAsync(evidence)));
        Assert.Single(s.Repo.Systems);
        Assert.Empty(s.Auth.Calls);
    }

    [Fact]
    public async Task E_CancellationWhileMutationPending_DoesNotRetry()
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource<AiConsentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cts.Token.Register(() => completion.TrySetCanceled(cts.Token));
        s.Repo.ApplyHandler = (_, _, token) =>
        {
            Assert.Equal(cts.Token, token);
            return completion.Task;
        };
        var pending = s.Service.ApplyAsync(Start(), cts.Token);
        Assert.Single(s.Repo.Mutations);
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(s.Repo.Mutations);
    }

    private sealed class Setup
    {
        public Authorization Auth { get; } = new();
        public Gate Gate { get; } = new();
        public Repository Repo { get; } = new();
        public AiPrivacyNoticeGuard Privacy { get; }
        public AiContextualConsentService Service { get; }
        public Setup() { Auth.Actor = Actor(); Gate.Outcome = AiPrivacyNoticeGateOutcome.Satisfied; Repo.Lifecycle = Live(); Privacy = new(Auth, Gate); Service = new(Auth, Privacy, Repo); }
    }
    private sealed class Authorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }
        public Action<CancellationToken>? Callback { get; set; }
        public List<(AiModule Module, CancellationToken Token)> Calls { get; } = new();
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        { Calls.Add((module, cancellationToken)); Callback?.Invoke(cancellationToken); return Task.FromResult(Actor); }
    }
    private sealed class Gate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        public Action<CancellationToken>? Callback { get; set; }
        public List<CancellationToken> Calls { get; } = new();
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        { Calls.Add(cancellationToken); Callback?.Invoke(cancellationToken); return Task.FromResult(Outcome); }
    }
    private sealed class Repository : IAiContextualConsentRepository
    {
        public AiConsentLifecycle? Lifecycle { get; set; }
        public AiConsentResult Result { get; set; } = new(AiConsentOutcome.Unknown);
        public Action<string, CancellationToken>? Callback { get; set; }
        public Func<AiConsentRequest, AiConsentLifecycle, CancellationToken, Task<AiConsentResult>>? ApplyHandler { get; set; }
        public Func<AiConsentSystemInvalidation, AiConsentSystemResult> SystemHandler { get; set; } = r => new(r, AiConsentSystemOutcome.Denied);
        public List<(AiConsentRequest Request, AiConsentLifecycle Lifecycle, CancellationToken Token)> Mutations { get; } = new();
        public List<AiConsentPendingReference> Pending { get; } = new();
        public List<AiConsentSystemInvalidation> Systems { get; } = new();
        public Task<AiConsentLifecycle?> ReadLifecycleAsync(Andril.AI.DTOs.AiOperationReference operation, CancellationToken cancellationToken = default)
        { Callback?.Invoke("lifecycle", cancellationToken); return Task.FromResult(Lifecycle); }
        public Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default)
        { Mutations.Add((request, lifecycle, cancellationToken)); Callback?.Invoke("apply", cancellationToken); return ApplyHandler?.Invoke(request, lifecycle, cancellationToken) ?? Task.FromResult(Result); }
        public Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default)
        { Callback?.Invoke("status", cancellationToken); return Task.FromResult(Result); }
        public Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default)
        { Pending.Add(pending); Callback?.Invoke("pending", cancellationToken); return Task.FromResult(Result); }
        public Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default)
        { Systems.Add(evidence); Callback?.Invoke("system", cancellationToken); return Task.FromResult(SystemHandler(evidence)); }
    }
}
