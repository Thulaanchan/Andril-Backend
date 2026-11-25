using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatOperationEvidenceFreshnessTests
{
    private static readonly Guid Subject = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid Account = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid Profile = Guid.Parse("33000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation = Guid.Parse("44000000-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ChatAuthorizationRequest Request =
        new(ChatOperation.SubmitMessage, ChatModule.Social, Conversation);

    [Theory]
    [InlineData("default-both")]
    [InlineData("default-observation")]
    [InlineData("default-expiry")]
    [InlineData("min-observation")]
    [InlineData("min-expiry")]
    [InlineData("max-observation")]
    [InlineData("max-expiry")]
    [InlineData("expired")]
    [InlineData("expiry-at-now")]
    [InlineData("future-observation")]
    [InlineData("pre-invocation")]
    [InlineData("expiry-before-observation")]
    [InlineData("expiry-at-observation")]
    [InlineData("reversed-clock")]
    [InlineData("missing-binding")]
    [InlineData("inconsistent-currentness")]
    public async Task InvalidFreshnessCannotEstablishExplicitDenial(string scenario)
    {
        var clock = new EvaluationClock { End = scenario == "reversed-clock" ? Start.AddTicks(-1) : Start.AddTicks(2) };
        var reader = new Reader
        {
            Transform = evidence => evidence with
            {
                Binding = scenario switch
                {
                    "default-both" => evidence.Binding! with { ObservedAtUtc = default, ValidUntilUtc = default },
                    "default-observation" => evidence.Binding! with { ObservedAtUtc = default },
                    "default-expiry" => evidence.Binding! with { ValidUntilUtc = default },
                    "min-observation" => evidence.Binding! with { ObservedAtUtc = DateTimeOffset.MinValue },
                    "min-expiry" => evidence.Binding! with { ValidUntilUtc = DateTimeOffset.MinValue },
                    "max-observation" => evidence.Binding! with { ObservedAtUtc = DateTimeOffset.MaxValue },
                    "max-expiry" => evidence.Binding! with { ValidUntilUtc = DateTimeOffset.MaxValue },
                    "expired" => evidence.Binding! with { ValidUntilUtc = Start.AddTicks(1) },
                    "expiry-at-now" => evidence.Binding! with { ValidUntilUtc = clock.End },
                    "future-observation" => evidence.Binding! with { ObservedAtUtc = clock.End.AddTicks(1) },
                    "pre-invocation" => evidence.Binding! with { ObservedAtUtc = Start.AddTicks(-1) },
                    "expiry-before-observation" => evidence.Binding! with { ValidUntilUtc = Start.AddTicks(-1) },
                    "expiry-at-observation" => evidence.Binding! with { ValidUntilUtc = Start },
                    "missing-binding" => null,
                    "inconsistent-currentness" => evidence.Binding! with { IsCurrent = null },
                    _ => evidence.Binding
                }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader, clock).AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("blocked")]
    [InlineData("revoked")]
    public async Task FiniteFreshProhibitionRemainsDenied(string prohibition)
    {
        var reader = new Reader
        {
            Transform = evidence => evidence with
            {
                Outcome = prohibition == "denied" ? ChatAuthorizationOutcome.Denied : ChatAuthorizationOutcome.Allowed,
                Binding = evidence.Binding! with
                {
                    IsBlocked = prohibition == "blocked",
                    IsRevoked = prohibition == "revoked"
                }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Denied, await Policy(reader).AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("revoked")]
    public async Task SentinelExpiryInvalidatesBlockAndRevocation(string prohibition)
    {
        var reader = new Reader
        {
            Transform = evidence => evidence with
            {
                Outcome = ChatAuthorizationOutcome.Allowed,
                Binding = evidence.Binding! with
                {
                    ValidUntilUtc = DateTimeOffset.MaxValue,
                    IsBlocked = prohibition == "blocked",
                    IsRevoked = prohibition == "revoked"
                }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData("evaluation")]
    [InlineData("binding-request")]
    [InlineData("module")]
    [InlineData("profile")]
    public async Task FiniteFreshEvidenceStillRequiresExactBindings(string mismatch)
    {
        var reader = new Reader
        {
            Transform = evidence => mismatch switch
            {
                "evaluation" => evidence with { EvaluationId = Subject },
                "binding-request" => evidence with { Binding = evidence.Binding! with { RequestId = Subject } },
                "module" => evidence with { Binding = evidence.Binding! with { ProfileModule = ChatModule.Learning } },
                _ => evidence with { Binding = evidence.Binding! with { ModuleProfileId = Subject } }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Request));
    }

    [Fact]
    public async Task PositiveFiniteEvidenceRemainsUnavailable()
    {
        var reader = new Reader { Transform = evidence => evidence with { Outcome = ChatAuthorizationOutcome.Allowed } };
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactObservationAndExpiryBoundariesUseUtcInstants(bool observationAtEnd)
    {
        var clock = new EvaluationClock { End = Start.AddTicks(2) };
        var reader = new Reader
        {
            Transform = evidence => evidence with
            {
                Binding = evidence.Binding! with
                {
                    ObservedAtUtc = (observationAtEnd ? clock.End : Start).ToOffset(TimeSpan.FromHours(5.5)),
                    ValidUntilUtc = clock.End.AddTicks(1).ToOffset(TimeSpan.FromHours(-4))
                }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Denied, await Policy(reader, clock).AuthorizeAsync(Request));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringReadPreservesCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader { DuringRead = () => cancellation.Cancel() };
        var policy = Policy(reader);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => policy.AuthorizeAsync(Request, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => policy.AuthorizeAsync(Request, cancellation.Token));
        Assert.Equal(1, reader.OperationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyFailureCannotEstablishDenialOrGrant(bool dependencyCancellation)
    {
        var reader = new Reader
        {
            Failure = dependencyCancellation ? new OperationCanceledException("synthetic") : new InvalidOperationException("synthetic")
        };
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Request));
    }

    [Fact]
    public async Task LegacyEvaluatorRetainsExistingMaxValueBehaviorAndDenyAll()
    {
        var reader = new Reader
        {
            LegacyTransform = evidence => evidence with { ValidUntilUtc = DateTimeOffset.MaxValue }
        };
        var actor = await Policy(reader).AuthorizeConversationAsync(ChatModule.Social, Conversation);
        Assert.NotNull(actor);
        Assert.Equal(Profile, actor.ModuleProfileId);
        Assert.Equal(0, reader.OperationCalls);
        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        Assert.Null(await fallback.AuthorizeConversationAsync(ChatModule.Social, Conversation));
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await fallback.AuthorizeAsync(Request));
    }

    private static ChatConversationAccessPolicy Policy(Reader reader, EvaluationClock? clock = null) =>
        new(new User(), reader, clock ?? new EvaluationClock());

    private sealed class User : ICurrentUser
    {
        public Guid? UserId => Subject;
        public bool IsAuthenticated => true;
    }

    private sealed class EvaluationClock : TimeProvider
    {
        private int _calls;
        internal DateTimeOffset End { get; init; } = Start;
        public override DateTimeOffset GetUtcNow() => _calls++ == 0 ? Start : End;
    }

    private sealed class Reader : IChatConversationAccessEvidenceReader
    {
        internal Func<ChatOperationAccessEvidence, ChatOperationAccessEvidence> Transform { get; init; } = evidence => evidence;
        internal Func<ChatConversationAccessEvidence, ChatConversationAccessEvidence> LegacyTransform { get; init; } = evidence => evidence;
        internal Action? DuringRead { get; init; }
        internal Exception? Failure { get; init; }
        internal int OperationCalls { get; private set; }

        public Task<ChatOperationAccessEvidence?> ReadOperationAsync(
            Guid subject, ChatAuthorizationRequest request, Guid evaluationId, CancellationToken cancellationToken)
        {
            OperationCalls++;
            DuringRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            var evidence = new ChatOperationAccessEvidence(evaluationId, request, ChatAuthorizationOutcome.Denied, Binding(subject, evaluationId));
            return Task.FromResult<ChatOperationAccessEvidence?>(Transform(evidence));
        }

        public Task<ChatConversationAccessEvidence?> ReadAsync(
            Guid subject, ChatModule module, Guid conversation, Guid evaluationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ChatConversationAccessEvidence?>(LegacyTransform(Binding(subject, evaluationId)));
        }

        private static ChatConversationAccessEvidence Binding(Guid subject, Guid evaluationId) => new()
        {
            RequestId = evaluationId, AccountSubjectId = subject,
            AccountId = Account, ProfileAccountId = Account, ModuleProfileId = Profile, ProfileModule = ChatModule.Social,
            Conversation = ChatConversation.Create(ChatModule.Social, Conversation,
                [ChatParticipantReference.Create(ChatModule.Social, Account, Profile)]),
            // Test-owned finite window. No production TTL is defined.
            ObservedAtUtc = Start, ValidUntilUtc = Start.AddTicks(10), IsCurrent = true,
            IsProfileActive = true, IsProfileEligible = true, IsParticipationAuthorized = true,
            IsModuleCommunicationEligible = true, IsBlocked = false, IsRevoked = false,
            IsAdult = true, HasSocialOptIn = true, HasMutualMatch = true
        };
    }
}
