using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Providers.OpenAi;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiGenerationServiceTests
{
    private sealed class Authorization(AiAuthorizedActor actor, List<string> events) : IAiActorAuthorization
    {
        internal AiAuthorizedActor? Current = actor;
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        { events.Add("authorize"); return Task.FromResult(Current); }
    }
    private sealed class Privacy(List<string> events) : IAiPrivacyNoticeGate
    {
        internal AiPrivacyNoticeGateOutcome Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        { events.Add("privacy"); return Task.FromResult(Outcome); }
    }
    private sealed class Fixture
    {
        internal readonly List<string> Events = [];
        internal readonly AiAuthorizedActor Actor;
        internal readonly Authorization Auth;
        internal readonly Privacy Gate;
        internal readonly AiTextResponseProviderTests.Counter Counter = new();
        internal readonly AiPreAdmissionPromptContext Context;
        internal readonly AiOperationAdmissionRequest Request;
        internal readonly AiGenerationService Service;
        internal Fixture(AiModule module = AiModule.Learning)
        {
            Actor = new(Guid.NewGuid(), Guid.NewGuid(), module); Auth = new(Actor, Events); Gate = new(Events);
            var conversation = Guid.NewGuid();
            Context = AiPreAdmissionPromptContext.ForNewConversation(Actor, conversation, Guid.NewGuid());
            Request = new(module, conversation, Guid.NewGuid(), AiTextResponseProviderTests.Private);
            Counter.Count = (r, p, _) => { Events.Add("measure"); return Task.FromResult(AiTextResponseProviderTests.Measurement(r, p)); };
            Service = new(new(Auth, Gate), new(Counter));
        }
        internal Task<AiGenerationPreparation> Prepare(CancellationToken token = default) =>
            Service.PrepareAsync(Request, Context, Guid.NewGuid(), 100, token);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task FreshGatesSurroundPreparationButCannotAdmitExecuteOrDeliver(AiModule module)
    {
        var f = new Fixture(module); var result = await f.Prepare();
        Assert.Equal(new[] { "authorize", "privacy", "measure", "authorize", "privacy" }, f.Events);
        Assert.True(result.TransactionAuthorityUnavailable);
        Assert.Equal(f.Request.UserText, Assert.Single(result.Prompt.Prompt.Messages).Text);
        Assert.Same(f.Counter.Payload, result.ProviderRequest.Payload.MeasurementPayload);
        Assert.Equal(result.Prompt.Measurement.Result, result.ProviderRequest.Measurement);
        Assert.True(result.Prompt.MatchesCurrentContext(f.Actor, f.Request.ConversationId, f.Context.Revision));
        Assert.False(result.Prompt.MatchesCurrentContext(f.Actor, f.Request.ConversationId, Guid.NewGuid()));
        Assert.DoesNotContain(f.Request.UserText, result.ToString());
        Assert.False(typeof(AiGenerationService).IsPublic);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("inactive")]
    public async Task ActorChangesRejectBeforeCounting(string defect)
    {
        var f = new Fixture();
        f.Auth.Current = defect switch
        {
            "foreign" => f.Actor with { UserId = Guid.NewGuid() },
            "profile" => f.Actor with { ModuleProfileId = Guid.NewGuid() },
            "module" => f.Actor with { Module = AiModule.Hobbies },
            _ => null
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Prepare());
        Assert.Equal(0, f.Counter.Calls);
        Assert.Equal(new[] { "authorize" }, f.Events);
    }

    [Theory]
    [InlineData(AiPrivacyNoticeGateOutcome.Denied)]
    [InlineData(AiPrivacyNoticeGateOutcome.ConsentRequired)]
    [InlineData(AiPrivacyNoticeGateOutcome.NoticeRequired)]
    public async Task PrivacyDenialNeverMeasures(AiPrivacyNoticeGateOutcome outcome)
    {
        var f = new Fixture(); f.Gate.Outcome = outcome;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Prepare());
        Assert.Equal(0, f.Counter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationDuringCountingDiscardsPreparation(bool actorChanged)
    {
        var f = new Fixture();
        f.Counter.Count = (r, p, _) =>
        {
            if (actorChanged) f.Auth.Current = f.Actor with { ModuleProfileId = Guid.NewGuid() };
            else f.Gate.Outcome = AiPrivacyNoticeGateOutcome.Denied;
            return Task.FromResult(AiTextResponseProviderTests.Measurement(r, p));
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Prepare());
        Assert.Equal(1, f.Counter.Calls);
    }

    [Fact]
    public async Task ProductionCounterAndRepeatedOperationsNeverImplySuccessfulAdmission()
    {
        var f = new Fixture();
        var actual = new AiGenerationService(new(f.Auth, f.Gate), new(new AiProviderTokenCounter()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => actual.PrepareAsync(f.Request, f.Context, Guid.NewGuid(), 100));
        var first = await f.Prepare(); var second = await f.Prepare();
        Assert.True(first.TransactionAuthorityUnavailable); Assert.True(second.TransactionAuthorityUnavailable);
        Assert.Equal(2, f.Counter.Calls); // Repeated read-only preparation, never dispatch/retry.
    }

    [Theory]
    [InlineData("module")]
    [InlineData("conversation")]
    [InlineData("input")]
    public async Task RequestScopeAndLimitsDenyBeforeMeasurement(string defect)
    {
        var f = new Fixture();
        var request = defect switch
        {
            "module" => f.Request with { Module = AiModule.Hobbies },
            "conversation" => f.Request with { ConversationId = Guid.NewGuid() },
            _ => f.Request with { UserText = new string('x', 501) }
        };
        var error = await Record.ExceptionAsync(() => f.Service.PrepareAsync(request, f.Context, Guid.NewGuid(), 100));
        Assert.NotNull(error); Assert.Equal(0, f.Counter.Calls); Assert.Empty(f.Events);
        Assert.DoesNotContain(request.UserText, error.ToString());
    }

    [Fact]
    public async Task CancelledPreparationNeverInvokesGatesOrCounter()
    {
        var f = new Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Prepare(cts.Token));
        Assert.Empty(f.Events); Assert.Equal(0, f.Counter.Calls);
    }
}
