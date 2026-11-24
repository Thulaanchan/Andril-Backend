using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatConversationAccessPolicyTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task CompleteSyntheticEvidence_ShouldReturnExactActorWithoutChangingProductionFallback(ChatModule module)
    {
        var f = new Fixture();

        var actor = await f.Policy.AuthorizeConversationAsync(module, f.ConversationId);

        Assert.NotNull(actor);
        Assert.Equal(f.User.UserId, actor.VerifiedSubjectId);
        Assert.Equal(f.AccountId, actor.AccountId);
        Assert.Equal(f.ProfileId, actor.ModuleProfileId);
        Assert.Equal(module, actor.Module);
        Assert.Equal(f.ConversationId, actor.ConversationId);
        Assert.Equal(1, f.Reader.Calls);
        Assert.Null(await new DenyAllChatAccessAuthorizer().AuthorizeConversationAsync(module, f.ConversationId));
    }

    [Theory]
    [InlineData("missing-context")]
    [InlineData("unauthenticated")]
    [InlineData("missing-subject")]
    [InlineData("empty-subject")]
    public async Task MissingVerifiedIdentity_ShouldDenyBeforeReadingEvidence(string scenario)
    {
        var f = new Fixture();
        f.User.IsAuthenticated = scenario != "unauthenticated";
        if (scenario == "missing-subject") f.User.UserId = null;
        if (scenario == "empty-subject") f.User.UserId = Guid.Empty;
        var policy = new ChatConversationAccessPolicy(scenario == "missing-context" ? null : f.User, f.Reader, f.Clock);

        Assert.Null(await policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
        Assert.Equal(0, f.Reader.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task UnknownModule_ShouldDenyBeforeReadingEvidence(int module)
    {
        var f = new Fixture();

        Assert.Null(await f.Policy.AuthorizeConversationAsync((ChatModule)module, f.ConversationId));
        Assert.Equal(0, f.Reader.Calls);
    }

    [Fact]
    public async Task EmptyConversation_ShouldDenyBeforeReadingEvidence()
    {
        var f = new Fixture();

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Learning, Guid.Empty));
        Assert.Equal(0, f.Reader.Calls);
    }

    [Theory]
    [InlineData("empty-subject")]
    [InlineData("different-subject")]
    [InlineData("empty-account")]
    [InlineData("empty-profile")]
    [InlineData("missing-owner")]
    [InlineData("different-owner")]
    [InlineData("different-profile-module")]
    [InlineData("unknown-profile-module")]
    [InlineData("missing-conversation")]
    [InlineData("different-conversation")]
    [InlineData("different-conversation-module")]
    [InlineData("nonparticipant")]
    [InlineData("only-account-matches")]
    [InlineData("only-profile-matches")]
    public async Task MissingOrMismatchedIdentityAndParticipation_ShouldReturnOnlyNull(string scenario)
    {
        var f = new Fixture();
        f.Reader.Transform = e => scenario switch
        {
            "empty-subject" => e with { AccountSubjectId = Guid.Empty },
            "different-subject" => e with { AccountSubjectId = Guid.NewGuid() },
            "empty-account" => e with { AccountId = Guid.Empty, ProfileAccountId = Guid.Empty },
            "empty-profile" => e with { ModuleProfileId = Guid.Empty },
            "missing-owner" => e with { ProfileAccountId = Guid.Empty },
            "different-owner" => e with { ProfileAccountId = Guid.NewGuid() },
            "different-profile-module" => e with { ProfileModule = ChatModule.Learning },
            "unknown-profile-module" => e with { ProfileModule = (ChatModule)99 },
            "missing-conversation" => e with { Conversation = null },
            "different-conversation" => e with { Conversation = f.Conversation(ChatModule.Social, Guid.NewGuid()) },
            "different-conversation-module" => e with { Conversation = f.Conversation(ChatModule.Hobbies, f.ConversationId) },
            "nonparticipant" => e with { Conversation = ChatConversation.Create(ChatModule.Social, f.ConversationId, []) },
            "only-account-matches" => e with
            {
                Conversation = ChatConversation.Create(ChatModule.Social, f.ConversationId,
                    [ChatParticipantReference.Create(ChatModule.Social, f.AccountId, Guid.NewGuid())])
            },
            "only-profile-matches" => e with
            {
                Conversation = ChatConversation.Create(ChatModule.Social, f.ConversationId,
                    [ChatParticipantReference.Create(ChatModule.Social, Guid.NewGuid(), f.ProfileId)])
            },
            _ => throw new InvalidOperationException("Unknown synthetic scenario.")
        };

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
        Assert.Equal(1, f.Reader.Calls);
    }

    public static IEnumerable<object[]> CommonEvidenceFailures()
    {
        string[] affirmative = ["current", "active", "eligible", "participation", "module-eligibility"];
        foreach (var module in Enum.GetValues<ChatModule>())
        {
            foreach (var flag in affirmative)
            {
                yield return [module, flag, false];
                yield return [module, flag, null!];
            }

            foreach (var flag in new[] { "blocked", "revoked" })
            {
                yield return [module, flag, true];
                yield return [module, flag, null!];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommonEvidenceFailures))]
    public async Task NegativeOrMissingCommonEvidence_ShouldFailClosed(ChatModule module, string flag, bool? value)
    {
        var f = new Fixture();
        f.Reader.Transform = e => SetFlag(e, flag, value);

        Assert.Null(await f.Policy.AuthorizeConversationAsync(module, f.ConversationId));
        Assert.Equal(1, f.Reader.Calls);
    }

    [Theory]
    [InlineData("adult", false)]
    [InlineData("adult", null)]
    [InlineData("opt-in", false)]
    [InlineData("opt-in", null)]
    [InlineData("mutual-match", false)]
    [InlineData("mutual-match", null)]
    public async Task SocialUnderageOptOutOrMissingMutualMatch_ShouldDeny(string flag, bool? value)
    {
        var f = new Fixture();
        f.Reader.Transform = e => SetFlag(e, flag, value);

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Fact]
    public async Task OneSidedInterest_ShouldNotSubstituteForMutualMatch()
    {
        var f = new Fixture();
        // One-sided interest cannot confirm the reader's mutual-match check.
        f.Reader.Transform = e => e with { HasMutualMatch = false };

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Theory]
    [InlineData(ChatModule.Learning, false)]
    [InlineData(ChatModule.Learning, null)]
    [InlineData(ChatModule.Hobbies, false)]
    [InlineData(ChatModule.Hobbies, null)]
    public async Task LearningAndHobbies_ShouldRequireTheirOwnEligibilityWithoutSocialMatchRules(ChatModule module, bool? value)
    {
        var f = new Fixture();
        f.Reader.Transform = e => e with { IsAdult = value, HasSocialOptIn = value, HasMutualMatch = value };

        Assert.NotNull(await f.Policy.AuthorizeConversationAsync(module, f.ConversationId));

        f.Reader.Transform = e => e with { IsModuleCommunicationEligible = null };
        Assert.Null(await f.Policy.AuthorizeConversationAsync(module, f.ConversationId));
    }

    [Theory]
    [InlineData("wrong-request")]
    [InlineData("empty-request")]
    [InlineData("stale-read")]
    [InlineData("future-read")]
    [InlineData("missing-read-time")]
    [InlineData("expired")]
    [InlineData("expires-now")]
    [InlineData("missing-expiry")]
    public async Task StaleInvalidOrUnboundEvidence_ShouldDeny(string scenario)
    {
        var f = new Fixture();
        f.Reader.Transform = e => scenario switch
        {
            "wrong-request" => e with { RequestId = Guid.NewGuid() },
            "empty-request" => e with { RequestId = Guid.Empty },
            "stale-read" => e with { ObservedAtUtc = f.Clock.Now.AddTicks(-1) },
            "future-read" => e with { ObservedAtUtc = f.Clock.Now.AddTicks(1) },
            "missing-read-time" => e with { ObservedAtUtc = default },
            "expired" => e with { ValidUntilUtc = f.Clock.Now.AddTicks(-1) },
            "expires-now" => e with { ValidUntilUtc = f.Clock.Now },
            "missing-expiry" => e with { ValidUntilUtc = default },
            _ => throw new InvalidOperationException("Unknown synthetic scenario.")
        };

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Theory]
    [InlineData("blocked", true)]
    [InlineData("revoked", true)]
    [InlineData("current", false)]
    [InlineData("mutual-match", false)]
    [InlineData("opt-in", false)]
    [InlineData("active", false)]
    public async Task BlockUnmatchAndRevocation_ShouldBeRecheckedOnEveryCall(string flag, bool value)
    {
        var f = new Fixture();
        Assert.NotNull(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));

        f.Reader.Transform = e => SetFlag(e, flag, value);

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
        Assert.Equal(2, f.Reader.Calls);
        Assert.Equal(2, f.Reader.RequestIds.Distinct().Count());
    }

    [Fact]
    public async Task PreviouslySuccessfulEvidence_ShouldNotBeReusable()
    {
        var f = new Fixture();
        ChatConversationAccessEvidence? previous = null;
        f.Reader.Transform = e => previous ??= e;

        Assert.NotNull(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Fact]
    public async Task EvidenceExpiringDuringRead_ShouldDeny()
    {
        var f = new Fixture();
        f.Reader.Transform = e =>
        {
            f.Clock.Now = e.ValidUntilUtc;
            return e;
        };

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Theory]
    [InlineData("missing-reader")]
    [InlineData("missing-evidence")]
    [InlineData("empty-evidence")]
    [InlineData("reader-error")]
    [InlineData("dependency-cancelled")]
    [InlineData("null-task")]
    public async Task MissingOrUnavailableDependency_ShouldReturnPrivacySafeDenial(string scenario)
    {
        var f = new Fixture();
        if (scenario == "missing-evidence") f.Reader.Transform = _ => null;
        if (scenario == "empty-evidence") f.Reader.Transform = _ => new ChatConversationAccessEvidence();
        if (scenario == "reader-error") f.Reader.Failure = new InvalidOperationException($"Private dependency {f.AccountId} {f.ProfileId}");
        if (scenario == "dependency-cancelled") f.Reader.Failure = new OperationCanceledException("Private dependency timeout.");
        f.Reader.ReturnNullTask = scenario == "null-task";
        var policy = new ChatConversationAccessPolicy(f.User, scenario == "missing-reader" ? null : f.Reader, f.Clock);

        Assert.Null(await policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    [InlineData((ChatModule)0)]
    public async Task PreCancelledRequest_ShouldPropagateEvenForInvalidTargets(ChatModule module)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Policy.AuthorizeConversationAsync(module, Guid.Empty, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, f.Reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRead_ShouldPropagateEvenIfReaderIgnoresTokenOrThrows(bool throws)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Reader.Transform = e =>
        {
            cancellation.Cancel();
            if (throws) throw new InvalidOperationException("Synthetic dependency failure during cancellation.");
            return e;
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, f.Reader.LastToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationChangingDuringRead_ShouldDeny(bool changeSubject)
    {
        var f = new Fixture();
        f.Reader.Transform = e =>
        {
            if (changeSubject) f.User.UserId = Guid.NewGuid();
            else f.User.IsAuthenticated = false;
            return e;
        };

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
    }

    [Fact]
    public async Task DependencyCancellationMessage_ShouldNotDiscloseIdentifiers()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Reader.Transform = _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException($"Private {f.AccountId} {f.ProfileId}", cancellation.Token);
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain(f.AccountId.ToString(), error.Message);
        Assert.DoesNotContain(f.ProfileId.ToString(), error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task AuthenticationProviderFailure_ShouldDenyWithoutReadingDependencies()
    {
        var f = new Fixture();
        f.User.ThrowOnRead = true;

        Assert.Null(await f.Policy.AuthorizeConversationAsync(ChatModule.Social, f.ConversationId));
        Assert.Equal(0, f.Reader.Calls);
    }

    [Fact]
    public void EvidenceAndPolicy_ShouldRemainInternalAndDiagnosticOutputShouldBeOpaque()
    {
        Assert.False(typeof(ChatConversationAccessPolicy).IsVisible);
        Assert.False(typeof(ChatConversationAccessEvidence).IsVisible);
        Assert.False(typeof(IChatConversationAccessEvidenceReader).IsVisible);
        var evidence = new ChatConversationAccessEvidence { AccountId = Guid.NewGuid(), ModuleProfileId = Guid.NewGuid() };
        Assert.Equal(nameof(ChatConversationAccessEvidence), evidence.ToString());
    }

    private static ChatConversationAccessEvidence SetFlag(ChatConversationAccessEvidence evidence, string flag, bool? value) =>
        flag switch
        {
            "current" => evidence with { IsCurrent = value },
            "active" => evidence with { IsProfileActive = value },
            "eligible" => evidence with { IsProfileEligible = value },
            "participation" => evidence with { IsParticipationAuthorized = value },
            "module-eligibility" => evidence with { IsModuleCommunicationEligible = value },
            "blocked" => evidence with { IsBlocked = value },
            "revoked" => evidence with { IsRevoked = value },
            "adult" => evidence with { IsAdult = value },
            "opt-in" => evidence with { HasSocialOptIn = value },
            "mutual-match" => evidence with { HasMutualMatch = value },
            _ => throw new InvalidOperationException("Unknown synthetic flag.")
        };

    // All providers below are synthetic, confined to this test assembly and
    // deliberately absent from production dependency injection.
    private sealed class Fixture
    {
        internal SyntheticUser User { get; } = new();
        internal SyntheticClock Clock { get; } = new();
        internal Guid AccountId { get; } = Guid.NewGuid();
        internal Guid ProfileId { get; } = Guid.NewGuid();
        internal Guid ConversationId { get; } = Guid.NewGuid();
        internal SyntheticReader Reader { get; }
        internal ChatConversationAccessPolicy Policy { get; }

        internal Fixture()
        {
            Reader = new SyntheticReader(this);
            Policy = new ChatConversationAccessPolicy(User, Reader, Clock);
        }

        internal ChatConversation Conversation(ChatModule module, Guid conversationId) =>
            ChatConversation.Create(module, conversationId, [ChatParticipantReference.Create(module, AccountId, ProfileId)]);
    }

    private sealed class SyntheticUser : ICurrentUser
    {
        internal bool ThrowOnRead { get; set; }
        private bool _isAuthenticated = true;
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public bool IsAuthenticated
        {
            get => ThrowOnRead ? throw new InvalidOperationException("Synthetic authentication unavailable.") : _isAuthenticated;
            set => _isAuthenticated = value;
        }
    }

    private sealed class SyntheticClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SyntheticReader(Fixture fixture) : IChatConversationAccessEvidenceReader
    {
        internal int Calls { get; private set; }
        internal List<Guid> RequestIds { get; } = [];
        internal CancellationToken LastToken { get; private set; }
        internal Func<ChatConversationAccessEvidence, ChatConversationAccessEvidence?> Transform { get; set; } = e => e;
        internal Exception? Failure { get; set; }
        internal bool ReturnNullTask { get; set; }

        public Task<ChatConversationAccessEvidence?> ReadAsync(
            Guid verifiedSubjectId, ChatModule module, Guid conversationId, Guid requestId, CancellationToken cancellationToken)
        {
            Calls++;
            RequestIds.Add(requestId);
            LastToken = cancellationToken;
            if (Failure is not null) return Task.FromException<ChatConversationAccessEvidence?>(Failure);
            if (ReturnNullTask) return null!;
            var evidence = new ChatConversationAccessEvidence
            {
                RequestId = requestId,
                AccountSubjectId = verifiedSubjectId,
                AccountId = fixture.AccountId,
                ProfileAccountId = fixture.AccountId,
                ModuleProfileId = fixture.ProfileId,
                ProfileModule = module,
                Conversation = fixture.Conversation(module, conversationId),
                ObservedAtUtc = fixture.Clock.Now,
                ValidUntilUtc = fixture.Clock.Now.AddMinutes(1), // Synthetic expiry, not an approved production lifetime.
                IsCurrent = true,
                IsProfileActive = true,
                IsProfileEligible = true,
                IsParticipationAuthorized = true,
                IsModuleCommunicationEligible = true,
                IsBlocked = false,
                IsRevoked = false,
                IsAdult = true,
                HasSocialOptIn = true,
                HasMutualMatch = true
            };
            return Task.FromResult(Transform(evidence));
        }
    }
}
