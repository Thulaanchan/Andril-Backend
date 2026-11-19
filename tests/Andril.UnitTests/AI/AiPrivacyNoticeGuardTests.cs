using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Synthetic guard tests; no real privacy evidence, database or provider.</summary>
public sealed class AiPrivacyNoticeGuardTests
{
    [Fact]
    public async Task DefaultFakes_NeverGrantPermission()
    {
        var authorization = new Authorization();
        var gate = new PrivacyGate();
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireSatisfiedAsync(AiModule.Learning));
        Assert.Empty(gate.Calls);
        authorization.Actor = Actor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.RequireSatisfiedAsync(AiModule.Learning));

        Assert.Equal(2, authorization.Calls.Count);
        Assert.Single(gate.Calls);
    }

    [Theory]
    [InlineData(true, "authorization")]
    [InlineData(false, "privacyGate")]
    public void Constructor_RejectsNullDependency(bool authorization, string parameter)
    {
        var error = Assert.Throws<ArgumentNullException>(() => new AiPrivacyNoticeGuard(
            authorization ? null! : new Authorization(),
            authorization ? new PrivacyGate() : null!));

        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(AiModule.Learning, false)]
    [InlineData(AiModule.Hobbies, false)]
    [InlineData(AiModule.Social, false)]
    [InlineData(AiModule.Learning, true)]
    [InlineData(AiModule.Hobbies, true)]
    [InlineData(AiModule.Social, true)]
    public async Task Satisfied_PreservesFreshActorAndForwardsArguments(AiModule module, bool sameActor)
    {
        var actor = Actor(module);
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);
        using var cts = new CancellationTokenSource();
        // Record equality, rather than reference equality, establishes unchanged identity.
        var expected = actor with { };
        Assert.NotSame(actor, expected);

        var result = await Invoke(guard, expected, sameActor, cts.Token);

        Assert.Same(actor, result);
        Assert.Equal(expected.UserId, result.UserId);
        Assert.Equal(expected.ModuleProfileId, result.ModuleProfileId);
        Assert.Equal(module, result.Module);
        Assert.Equal((module, cts.Token), Assert.Single(authorization.Calls));
        var call = Assert.Single(gate.Calls);
        Assert.Same(actor, call.Actor);
        Assert.Equal(cts.Token, call.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task InvalidRequestedModule_DeniesBeforeDependencies(int module)
    {
        var authorization = new Authorization { Actor = Actor() };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            guard.RequireSatisfiedAsync((AiModule)module));

        Assert.Empty(authorization.Calls);
        Assert.Empty(gate.Calls);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("account", false)]
    [InlineData("profile", false)]
    [InlineData("both", false)]
    [InlineData("invalid-module", false)]
    [InlineData("changed-account", false)]
    [InlineData("changed-profile", false)]
    [InlineData("changed-module", false)]
    [InlineData("null", true)]
    [InlineData("account", true)]
    [InlineData("profile", true)]
    [InlineData("both", true)]
    [InlineData("invalid-module", true)]
    [InlineData("changed-account", true)]
    [InlineData("changed-profile", true)]
    [InlineData("changed-module", true)]
    public async Task AuthorizationResult_IsValidatedBeforePrivacy(string defect, bool sameActor)
    {
        var expected = Actor();
        var returned = Change(expected, defect);
        var authorization = new Authorization { Actor = returned };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        // Initial authorization may legitimately resolve any valid owner/profile.
        if (!sameActor && defect is "changed-account" or "changed-profile")
        {
            Assert.Same(returned, await Invoke(guard, expected, sameActor));
            Assert.Single(gate.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Invoke(guard, expected, sameActor));
            Assert.Empty(gate.Calls);
        }

        Assert.Equal((expected.Module, CancellationToken.None), Assert.Single(authorization.Calls));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("both")]
    [InlineData("invalid-module")]
    public async Task InvalidExpectedActor_DeniesBeforeDependencies(string defect)
    {
        var authorization = new Authorization { Actor = Actor() };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            guard.RequireSameActorSatisfiedAsync(Change(Actor(), defect)));

        Assert.Empty(authorization.Calls);
        Assert.Empty(gate.Calls);
    }

    [Theory]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.Denied, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.PolicyNotVerified, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.Unknown, false)]
    [InlineData((AiPrivacyNoticeGateOutcome)(-1), false)]
    [InlineData((AiPrivacyNoticeGateOutcome)99, false)]
    [InlineData((AiPrivacyNoticeGateOutcome)int.MinValue, false)]
    [InlineData((AiPrivacyNoticeGateOutcome)int.MaxValue, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.Denied, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.PolicyNotVerified, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.Unknown, true)]
    [InlineData((AiPrivacyNoticeGateOutcome)(-1), true)]
    [InlineData((AiPrivacyNoticeGateOutcome)99, true)]
    [InlineData((AiPrivacyNoticeGateOutcome)int.MinValue, true)]
    [InlineData((AiPrivacyNoticeGateOutcome)int.MaxValue, true)]
    public async Task NonSuccessOutcome_FailsClosed(AiPrivacyNoticeGateOutcome outcome, bool sameActor)
    {
        var actor = Actor();
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = outcome };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        await AssertOutcomeDenied(() => Invoke(guard, actor, sameActor), outcome);

        Assert.Single(authorization.Calls);
        Assert.Single(gate.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelled_CallsNeitherDependency(bool sameActor)
    {
        var authorization = new Authorization { Actor = Actor() };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Invoke(guard, Actor(), sameActor, cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Empty(authorization.Calls);
        Assert.Empty(gate.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationWhileDependencyPending_Propagates(bool privacyStage, bool sameActor)
    {
        var actor = Actor();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        if (privacyStage)
            gate.Handler = async (_, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return AiPrivacyNoticeGateOutcome.Satisfied;
            };
        else
            authorization.Handler = async (_, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return actor;
            };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        var pending = Invoke(guard, actor, sameActor, cts.Token);
        await entered.Task;
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal((actor.Module, cts.Token), Assert.Single(authorization.Calls));
        Assert.Equal(privacyStage ? 1 : 0, gate.Calls.Count);
        if (privacyStage) Assert.Equal(cts.Token, gate.Calls[0].Token);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationBeforeSuccessfulDependencyReturn_NeverGrants(bool privacyStage, bool sameActor)
    {
        var actor = Actor();
        using var cts = new CancellationTokenSource();
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        if (privacyStage)
            gate.Handler = (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(AiPrivacyNoticeGateOutcome.Satisfied);
            };
        else
            authorization.Handler = (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult<AiAuthorizedActor?>(actor);
            };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Invoke(guard, actor, sameActor, cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal((actor.Module, cts.Token), Assert.Single(authorization.Calls));
        Assert.Equal(privacyStage ? 1 : 0, gate.Calls.Count);
        if (privacyStage) Assert.Equal(cts.Token, gate.Calls[0].Token);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DependencyFailure_PropagatesOriginalWithoutRetry(bool privacyStage, bool cancellation, bool sameActor)
    {
        var actor = Actor();
        using var cts = new CancellationTokenSource();
        Exception failure = cancellation
            ? new OperationCanceledException("Synthetic dependency cancellation", cts.Token)
            : new InvalidOperationException("Synthetic dependency failure");
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        if (privacyStage)
            gate.Handler = (_, _) => Task.FromException<AiPrivacyNoticeGateOutcome>(failure);
        else
            authorization.Handler = (_, _) => Task.FromException<AiAuthorizedActor?>(failure);
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        var error = await Record.ExceptionAsync(() => Invoke(guard, actor, sameActor, cts.Token));

        Assert.Same(failure, error);
        Assert.Equal((actor.Module, cts.Token), Assert.Single(authorization.Calls));
        Assert.Equal(privacyStage ? 1 : 0, gate.Calls.Count);
        if (privacyStage) Assert.Equal(cts.Token, gate.Calls[0].Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviousSuccess_DoesNotBypassAuthorizationRevocation(bool sameActor)
    {
        var actor = Actor();
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);
        Assert.Equal(actor, await Invoke(guard, actor, sameActor));
        authorization.Actor = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Invoke(guard, actor, sameActor));

        Assert.Equal(2, authorization.Calls.Count);
        Assert.Single(gate.Calls);
    }

    [Theory]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.PolicyNotVerified, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.Denied, false)]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.PolicyNotVerified, true)]
    [InlineData(AiPrivacyNoticeGateOutcome.Denied, true)]
    public async Task PreviousSuccess_DoesNotBypassChangedPrivacy(AiPrivacyNoticeGateOutcome outcome, bool sameActor)
    {
        var actor = Actor();
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);
        Assert.Equal(actor, await Invoke(guard, actor, sameActor));
        gate.Outcome = outcome;

        await AssertOutcomeDenied(() => Invoke(guard, actor, sameActor), outcome);

        Assert.Equal(2, authorization.Calls.Count);
        Assert.Equal(2, gate.Calls.Count);
    }

    [Theory]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task LearningSuccess_DoesNotBypassOtherModulePrivacy(AiModule module)
    {
        var learning = Actor();
        var other = Actor(module);
        var authorization = new Authorization { Actor = learning };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);
        Assert.Equal(learning, await guard.RequireSatisfiedAsync(AiModule.Learning));
        authorization.Actor = other;
        gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireSatisfiedAsync(module));

        Assert.Equal(2, authorization.Calls.Count);
        Assert.Equal((module, CancellationToken.None), authorization.Calls[1]);
        Assert.Equal(2, gate.Calls.Count);
        Assert.Same(other, gate.Calls[1].Actor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedSuccess_ReevaluatesBothDependencies(bool sameActor)
    {
        var actor = Actor();
        var authorization = new Authorization { Actor = actor };
        var gate = new PrivacyGate { Outcome = AiPrivacyNoticeGateOutcome.Satisfied };
        var guard = new AiPrivacyNoticeGuard(authorization, gate);

        for (var i = 0; i < 3; i++)
            Assert.Equal(actor, await Invoke(guard, actor, sameActor));

        Assert.Equal(3, authorization.Calls.Count);
        Assert.Equal(3, gate.Calls.Count);
        Assert.All(authorization.Calls, call => Assert.Equal((actor.Module, CancellationToken.None), call));
        Assert.All(gate.Calls, call => Assert.Equal((actor, CancellationToken.None), call));
    }

    private static async Task AssertOutcomeDenied(Func<Task<AiAuthorizedActor>> action, AiPrivacyNoticeGateOutcome outcome)
    {
        if (outcome is AiPrivacyNoticeGateOutcome.NoticeRequired or AiPrivacyNoticeGateOutcome.ConsentRequired or AiPrivacyNoticeGateOutcome.Denied)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(action);
    }

    private static Task<AiAuthorizedActor> Invoke(AiPrivacyNoticeGuard guard, AiAuthorizedActor actor,
        bool sameActor, CancellationToken token = default) => sameActor
        ? guard.RequireSameActorSatisfiedAsync(actor, token)
        : guard.RequireSatisfiedAsync(actor.Module, token);

    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse($"20000000-0000-0000-0000-{(int)module:D12}"), module);

    private static AiAuthorizedActor? Change(AiAuthorizedActor actor, string defect) => defect switch
    {
        "null" => null,
        "account" => actor with { UserId = Guid.Empty },
        "profile" => actor with { ModuleProfileId = Guid.Empty },
        "both" => actor with { UserId = Guid.Empty, ModuleProfileId = Guid.Empty },
        "invalid-module" => actor with { Module = (AiModule)99 },
        "changed-account" => actor with { UserId = Guid.Parse("30000000-0000-0000-0000-000000000001") },
        "changed-profile" => actor with { ModuleProfileId = Guid.Parse("40000000-0000-0000-0000-000000000001") },
        "changed-module" => actor with { Module = AiModule.Hobbies },
        _ => throw new ArgumentOutOfRangeException(nameof(defect))
    };

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

    private sealed class PrivacyGate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        public Func<AiAuthorizedActor, CancellationToken, Task<AiPrivacyNoticeGateOutcome>>? Handler { get; set; }
        public List<(AiAuthorizedActor Actor, CancellationToken Token)> Calls { get; } = new();

        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        {
            Calls.Add((actor, cancellationToken));
            return Handler?.Invoke(actor, cancellationToken) ?? Task.FromResult(Outcome);
        }
    }
}
