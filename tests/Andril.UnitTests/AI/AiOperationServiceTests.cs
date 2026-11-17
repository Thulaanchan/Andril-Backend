using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Unit boundary simulations only; no claim of PostgreSQL atomicity.</summary>
public sealed class AiOperationServiceTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(Id(1), Id(10 + (int)module), module);
    private static AiOperationAdmissionRequest Request(AiModule module = AiModule.Learning) => new(module, Id(20), Id(21), "Synthetic input");
    private static AiOperationReference Reference(AiModule module = AiModule.Learning) => new(Actor(module), Id(20), Id(21));
    private static AiOperationSnapshot State(AiModule module = AiModule.Learning)
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        return new(Reference(module), AiOperationStatus.Reserved, AiQuotaState.Reserved, AiDispatchState.NotDispatched,
            now, DateOnly.FromDateTime(now.UtcDateTime), Id(30), null);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("repository")]
    public void NullDependencies_AreRejected(string name)
    {
        var setup = new Setup();
        var error = Assert.Throws<ArgumentNullException>(() => new AiOperationService(
            name == "authorization" ? null! : setup.Auth,
            name == "privacy" ? null! : setup.Guard,
            name == "repository" ? null! : setup.Repo));
        Assert.Equal(name, error.ParamName);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task Admission_ForwardsFreshScopeAndToken(AiModule module)
    {
        var setup = new Setup();
        setup.Auth.Actor = Actor(module);
        setup.Repo.Admission = new(AiOperationOutcome.Admitted, State(module));
        using var cts = new CancellationTokenSource();

        var result = await setup.Service.AdmitAsync(Request(module), cts.Token);

        Assert.Equal(setup.Repo.Admission, result);
        Assert.Equal((Actor(module), Request(module), cts.Token), Assert.Single(setup.Repo.Admissions));
        Assert.Equal(2, setup.Auth.Calls.Count);
        Assert.All(setup.Auth.Calls, call => Assert.Equal((module, cts.Token), call));
        Assert.Equal((Actor(module), cts.Token), Assert.Single(setup.Gate.Calls));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("changed")]
    public async Task InvalidAuthorization_NeverReachesRepository(string defect)
    {
        var setup = new Setup();
        setup.Auth.Actor = defect switch
        {
            "null" => null,
            "account" => Actor() with { UserId = Guid.Empty },
            "profile" => Actor() with { ModuleProfileId = Guid.Empty },
            "module" => Actor() with { Module = (AiModule)99 },
            _ => Actor(AiModule.Social)
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.AdmitAsync(Request()));
        Assert.Empty(setup.Repo.Admissions);
    }

    [Fact]
    public async Task IdentityChangeDuringPrivacyReauthorization_IsDenied()
    {
        var setup = new Setup();
        setup.Auth.Handler = (_, _) => Task.FromResult<AiAuthorizedActor?>(setup.Auth.Calls.Count == 1 ? Actor() : Actor() with { ModuleProfileId = Id(99) });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.AdmitAsync(Request()));
        Assert.Empty(setup.Gate.Calls);
        Assert.Empty(setup.Repo.Admissions);
    }

    [Theory]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired)]
    [InlineData(AiPrivacyNoticeGateOutcome.PolicyNotVerified)]
    [InlineData(AiPrivacyNoticeGateOutcome.Unknown)]
    public async Task PrivacyFailure_NeverReachesRepository(AiPrivacyNoticeGateOutcome outcome)
    {
        var setup = new Setup();
        setup.Gate.Outcome = outcome;
        var failure = await Record.ExceptionAsync(() => setup.Service.AdmitAsync(Request()));
        Assert.NotNull(failure);
        Assert.Empty(setup.Repo.Admissions);
    }

    [Theory]
    [InlineData(AiOperationOutcome.NotAllowlisted)]
    [InlineData(AiOperationOutcome.DailyQuotaExhausted)]
    [InlineData(AiOperationOutcome.AccountRateLimited)]
    [InlineData(AiOperationOutcome.GlobalRateLimited)]
    [InlineData(AiOperationOutcome.ConversationBusy)]
    [InlineData(AiOperationOutcome.IdempotencyConflict)]
    [InlineData(AiOperationOutcome.ConversationUnavailable)]
    [InlineData(AiOperationOutcome.Denied)]
    [InlineData(AiOperationOutcome.BudgetNotVerified)]
    [InlineData(AiOperationOutcome.ReconciliationRequired)]
    public async Task AuthoritativeDenials_AreReturnedWithoutRetry(AiOperationOutcome outcome)
    {
        var setup = new Setup();
        setup.Repo.Admission = new(outcome);
        Assert.Equal(outcome, (await setup.Service.AdmitAsync(Request())).Outcome);
        Assert.Single(setup.Repo.Admissions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("undefined")]
    [InlineData("missing-state")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("conversation")]
    [InlineData("operation")]
    [InlineData("state")]
    [InlineData("foreign-denial")]
    [InlineData("wrong-success")]
    public async Task MalformedRepositoryResult_FailsClosed(string defect)
    {
        var setup = new Setup();
        var state = State();
        var reference = state.Operation;
        reference = defect switch
        {
            "account" => reference with { Actor = Actor() with { UserId = Id(99) } },
            "profile" => reference with { Actor = Actor() with { ModuleProfileId = Id(99) } },
            "module" => Reference(AiModule.Social),
            "conversation" => reference with { ConversationId = Id(99) },
            "operation" => reference with { OperationId = Id(99) },
            _ => reference
        };
        setup.Repo.Admission = defect switch
        {
            "null" => null!,
            "unknown" => new(AiOperationOutcome.Unknown),
            "undefined" => new((AiOperationOutcome)99),
            "missing-state" => new(AiOperationOutcome.Admitted),
            "state" => new(AiOperationOutcome.Admitted, state with { Quota = AiQuotaState.Released }),
            "foreign-denial" => new(AiOperationOutcome.Denied, state),
            "wrong-success" => new(AiOperationOutcome.AuthorizedStatus, state),
            _ => new(AiOperationOutcome.Admitted, state with { Operation = reference })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.AdmitAsync(Request()));
        Assert.Single(setup.Repo.Admissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCompletedOrUncertainOperation_IsNotRedispatched(bool completed)
    {
        var setup = new Setup();
        var state = completed
            ? State() with { Status = AiOperationStatus.Completed, Quota = AiQuotaState.Committed, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) }
            : State() with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain };
        setup.Repo.Admission = new(AiOperationOutcome.Existing, state);
        Assert.Equal(setup.Repo.Admission, await setup.Service.AdmitAsync(Request()));
        Assert.Equal(setup.Repo.Admission, await setup.Service.AdmitAsync(Request()));
        Assert.Equal(2, setup.Repo.Admissions.Count);
        Assert.Empty(setup.Repo.Preparations);
    }

    [Fact]
    public async Task StatusLookup_IsIndependentOfQuotaAndPrivacyAdmission()
    {
        var setup = new Setup();
        setup.Gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;
        setup.Repo.Admission = new(AiOperationOutcome.DailyQuotaExhausted);
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus, State());
        using var cts = new CancellationTokenSource();

        Assert.Equal(setup.Repo.Status, await setup.Service.ReconcileAsync(Reference(), cts.Token));

        Assert.Equal((Reference(), cts.Token), Assert.Single(setup.Repo.Reconciliations));
        Assert.Empty(setup.Repo.Admissions);
        Assert.Empty(setup.Gate.Calls);
    }

    [Fact]
    public async Task ForeignStatusReference_IsDeniedBeforeLookup()
    {
        var setup = new Setup();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.ReconcileAsync(Reference() with { Actor = Actor() with { UserId = Id(99) } }));
        Assert.Empty(setup.Repo.Reconciliations);
    }

    [Theory]
    [InlineData("eligible", AiOperationOutcome.ReadyForManualRetry)]
    [InlineData("unreconciled", AiOperationOutcome.ReconciliationRequired)]
    [InlineData("active", AiOperationOutcome.RetryBlocked)]
    [InlineData("uncertain", AiOperationOutcome.ReconciliationRequired)]
    [InlineData("released", AiOperationOutcome.RetryBlocked)]
    [InlineData("prepared", AiOperationOutcome.RetryBlocked)]
    public async Task Retry_ReconcilesBeforeAtomicPreparation(string scenario, AiOperationOutcome expected)
    {
        var setup = new Setup();
        var failed = State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed };
        var state = scenario switch
        {
            "active" => State() with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, AttemptId = Id(40) },
            "uncertain" => State() with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain },
            "released" => failed with { Quota = AiQuotaState.Released },
            "prepared" => State(),
            _ => failed
        };
        setup.Repo.Status = scenario == "unreconciled" ? new(AiOperationOutcome.ReconciliationRequired) : new(AiOperationOutcome.AuthorizedStatus, state);
        setup.Repo.Retry = new(AiOperationOutcome.ReadyForManualRetry, State() with { Revision = Id(31) });
        using var cts = new CancellationTokenSource();

        Assert.Equal(expected, (await setup.Service.PrepareManualRetryAsync(new(Reference(), true), cts.Token)).Outcome);

        Assert.Equal((Reference(), cts.Token), Assert.Single(setup.Repo.Reconciliations));
        if (scenario == "eligible")
        {
            Assert.Equal((Reference(), failed.Revision, cts.Token), Assert.Single(setup.Repo.Preparations));
            Assert.Equal(2, setup.Gate.Calls.Count);
        }
        else Assert.Empty(setup.Repo.Preparations);
    }

    [Fact]
    public async Task ImplicitRetry_IsRejectedBeforeDependencies()
    {
        var setup = new Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.PrepareManualRetryAsync(new(Reference(), false)));
        Assert.Empty(setup.Auth.Calls);
        Assert.Empty(setup.Repo.Reconciliations);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("admission")]
    [InlineData("status")]
    [InlineData("retry")]
    public async Task DependencyFailure_PropagatesOriginalWithoutAutomaticRetry(string stage)
    {
        var setup = new Setup();
        var failure = new InvalidOperationException("Synthetic unavailable dependency");
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus,
            State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed });
        if (stage == "authorization") setup.Auth.Handler = (_, _) => Task.FromException<AiAuthorizedActor?>(failure);
        if (stage == "privacy") setup.Gate.Handler = (_, _) => Task.FromException<AiPrivacyNoticeGateOutcome>(failure);
        setup.Repo.FailureStage = stage;
        setup.Repo.Failure = failure;

        var error = await Record.ExceptionAsync(() => stage is "status" or "retry"
            ? setup.Service.PrepareManualRetryAsync(new(Reference(), true)) : setup.Service.AdmitAsync(Request()));

        Assert.Same(failure, error);
        Assert.InRange(setup.Repo.Admissions.Count, 0, 1);
        Assert.InRange(setup.Repo.Reconciliations.Count, 0, 1);
        Assert.InRange(setup.Repo.Preparations.Count, 0, 1);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("status")]
    [InlineData("retry")]
    public async Task AlreadyCancelled_DoesNotInvokeDependencies(string method)
    {
        var setup = new Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => method switch
        {
            "admission" => setup.Service.AdmitAsync(Request(), cts.Token),
            "status" => setup.Service.ReconcileAsync(Reference(), cts.Token),
            _ => setup.Service.PrepareManualRetryAsync(new(Reference(), true), cts.Token)
        });
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Empty(setup.Auth.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryCancellation_DoesNotRefundOrAutomaticallyRetry(bool throws)
    {
        var setup = new Setup();
        using var cts = new CancellationTokenSource();
        setup.Repo.OnAdmission = token =>
        {
            Assert.Equal(cts.Token, token);
            cts.Cancel(); // Simulates cancellation during a potentially durable admission.
            return throws ? Task.FromCanceled<AiOperationResult>(token)
                : Task.FromResult(new AiOperationResult(AiOperationOutcome.Admitted, State()));
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.AdmitAsync(Request(), cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(setup.Repo.Admissions);
        Assert.Empty(setup.Repo.Reconciliations);
        Assert.Empty(setup.Repo.Preparations);
    }

    [Theory]
    [InlineData("authorization", false)]
    [InlineData("authorization", true)]
    [InlineData("privacy", false)]
    [InlineData("privacy", true)]
    public async Task CancellationInsideIdentityDependencies_PreventsAdmission(string stage, bool throws)
    {
        var setup = new Setup();
        using var cts = new CancellationTokenSource();
        if (stage == "authorization")
            setup.Auth.Handler = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();
                return throws ? Task.FromCanceled<AiAuthorizedActor?>(token) : Task.FromResult<AiAuthorizedActor?>(Actor());
            };
        else
            setup.Gate.Handler = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();
                return throws ? Task.FromCanceled<AiPrivacyNoticeGateOutcome>(token) : Task.FromResult(AiPrivacyNoticeGateOutcome.Satisfied);
            };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.AdmitAsync(Request(), cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Empty(setup.Repo.Admissions);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("retry", false)]
    [InlineData("retry", true)]
    public async Task CancellationAtStatusOrPreparation_PreservesUncertainOutcome(string stage, bool throws)
    {
        var setup = new Setup();
        using var cts = new CancellationTokenSource();
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus,
            State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed });
        setup.Repo.Retry = new(AiOperationOutcome.ReadyForManualRetry, State() with { Revision = Id(31) });
        setup.Repo.OnResult = (current, token, result) =>
        {
            Assert.Equal(cts.Token, token);
            if (current != stage) return Task.FromResult(result);
            cts.Cancel();
            return throws ? Task.FromCanceled<AiOperationResult>(token) : Task.FromResult(result);
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            setup.Service.PrepareManualRetryAsync(new(Reference(), true), cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(setup.Repo.Reconciliations);
        Assert.Equal(stage == "retry" ? 1 : 0, setup.Repo.Preparations.Count);
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("unchanged-revision")]
    [InlineData("changed-day")]
    [InlineData("active-attempt")]
    public async Task MalformedRetryPreparation_IsRejected(string defect)
    {
        var setup = new Setup();
        var failed = State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed };
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus, failed);
        var prepared = State() with { Revision = Id(31) };
        prepared = defect switch
        {
            "wrong-owner" => prepared with { Operation = prepared.Operation with { Actor = Actor() with { UserId = Id(99) } } },
            "unchanged-revision" => prepared with { Revision = failed.Revision },
            "changed-day" => prepared with { AdmittedAtUtc = prepared.AdmittedAtUtc.AddDays(1), QuotaDay = prepared.QuotaDay.AddDays(1) },
            _ => prepared with { AttemptId = Id(40) }
        };
        setup.Repo.Retry = new(AiOperationOutcome.ReadyForManualRetry, prepared);

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.PrepareManualRetryAsync(new(Reference(), true)));

        Assert.Single(setup.Repo.Preparations);
    }

    [Fact]
    public async Task RepeatedRetry_ReconcilesAgainAndDoesNotPrepareTwice()
    {
        var setup = new Setup();
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus,
            State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed });
        setup.Repo.Retry = new(AiOperationOutcome.ReadyForManualRetry, State() with { Revision = Id(31) });
        Assert.Equal(AiOperationOutcome.ReadyForManualRetry,
            (await setup.Service.PrepareManualRetryAsync(new(Reference(), true))).Outcome);
        // Unit-level simulation of the repository's durable preparation transition.
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus, setup.Repo.Retry.Snapshot);

        Assert.Equal(AiOperationOutcome.RetryBlocked,
            (await setup.Service.PrepareManualRetryAsync(new(Reference(), true))).Outcome);

        Assert.Equal(2, setup.Repo.Reconciliations.Count);
        Assert.Single(setup.Repo.Preparations);
    }

    [Fact]
    public async Task RevocationAfterReconciliation_PreventsPreparation()
    {
        var setup = new Setup();
        setup.Repo.Status = new(AiOperationOutcome.AuthorizedStatus,
            State() with { Status = AiOperationStatus.ConfirmedFailed, Dispatch = AiDispatchState.ConclusivelyFailed });
        setup.Repo.OnResult = (_, _, result) =>
        {
            setup.Auth.Actor = null;
            return Task.FromResult(result);
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.PrepareManualRetryAsync(new(Reference(), true)));

        Assert.Single(setup.Repo.Reconciliations);
        Assert.Empty(setup.Repo.Preparations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedStatusResult_IsRejected(bool foreign)
    {
        var setup = new Setup();
        setup.Repo.Status = foreign
            ? new(AiOperationOutcome.AuthorizedStatus, State() with { Operation = Reference(AiModule.Social) })
            : new(AiOperationOutcome.Unknown);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReconcileAsync(Reference()));
        Assert.Single(setup.Repo.Reconciliations);
    }

    private sealed class Setup
    {
        public Authorization Auth { get; } = new();
        public Gate Gate { get; } = new();
        public Repository Repo { get; } = new();
        public AiPrivacyNoticeGuard Guard { get; }
        public AiOperationService Service { get; }
        public Setup()
        {
            // Explicit positive test setup; fake classes themselves default to denial.
            Auth.Actor = Actor();
            Gate.Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
            Guard = new(Auth, Gate);
            Service = new(Auth, Guard, Repo);
        }
    }

    private sealed class Authorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }
        public Func<AiModule, CancellationToken, Task<AiAuthorizedActor?>>? Handler { get; set; }
        public List<(AiModule Module, CancellationToken Token)> Calls { get; } = new();
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        {
            Calls.Add((module, cancellationToken));
            return Handler?.Invoke(module, cancellationToken) ?? Task.FromResult(Actor);
        }
    }

    private sealed class Gate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.Unknown;
        public Func<AiAuthorizedActor, CancellationToken, Task<AiPrivacyNoticeGateOutcome>>? Handler { get; set; }
        public List<(AiAuthorizedActor Actor, CancellationToken Token)> Calls { get; } = new();
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        {
            Calls.Add((actor, cancellationToken));
            return Handler?.Invoke(actor, cancellationToken) ?? Task.FromResult(Outcome);
        }
    }

    private sealed class Repository : IAiOperationRepository
    {
        public AiOperationResult Admission { get; set; } = new(AiOperationOutcome.Unknown);
        public AiOperationResult Status { get; set; } = new(AiOperationOutcome.Unknown);
        public AiOperationResult Retry { get; set; } = new(AiOperationOutcome.Unknown);
        public Func<CancellationToken, Task<AiOperationResult>>? OnAdmission { get; set; }
        public Func<string, CancellationToken, AiOperationResult, Task<AiOperationResult>>? OnResult { get; set; }
        public string? FailureStage { get; set; }
        public Exception Failure { get; set; } = new InvalidOperationException("Synthetic failure");
        public List<(AiAuthorizedActor Actor, AiOperationAdmissionRequest Request, CancellationToken Token)> Admissions { get; } = new();
        public List<(AiOperationReference Operation, CancellationToken Token)> Reconciliations { get; } = new();
        public List<(AiOperationReference Operation, Guid Revision, CancellationToken Token)> Preparations { get; } = new();
        public Task<AiOperationResult> AdmitAsync(AiAuthorizedActor actor, AiOperationAdmissionRequest request, CancellationToken cancellationToken = default)
        {
            Admissions.Add((actor, request, cancellationToken));
            return OnAdmission?.Invoke(cancellationToken) ?? Result("admission", Admission);
        }
        public Task<AiOperationResult> ReconcileAsync(AiOperationReference operation, CancellationToken cancellationToken = default)
        {
            Reconciliations.Add((operation, cancellationToken));
            return OnResult?.Invoke("status", cancellationToken, Status) ?? Result("status", Status);
        }
        public Task<AiOperationResult> PrepareManualRetryAsync(AiOperationReference operation, Guid reconciledRevision, CancellationToken cancellationToken = default)
        {
            Preparations.Add((operation, reconciledRevision, cancellationToken));
            return OnResult?.Invoke("retry", cancellationToken, Retry) ?? Result("retry", Retry);
        }
        private Task<AiOperationResult> Result(string stage, AiOperationResult result) => FailureStage == stage
            ? Task.FromException<AiOperationResult>(Failure) : Task.FromResult(result);
    }
}
