using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatOperationAuthorizationContractTests
{
    private static readonly Guid Subject = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Account = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Profile = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static ChatAuthorizationRequest Request(ChatOperation operation = ChatOperation.SubmitMessage) =>
        new(operation, ChatModule.Social, operation == ChatOperation.ListConversations ? null : Conversation);

    [Fact]
    public void OperationsAndDiagnosticsAreDistinctAndOpaque()
    {
        Assert.Equal(4, Enum.GetValues<ChatOperation>().Distinct().Count());
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, default);
        Assert.Equal(nameof(ChatAuthorizationRequest), Request().ToString());
        Assert.False(typeof(ChatOperationAccessEvidence).IsVisible);
    }

    [Theory]
    [InlineData(ChatOperation.ListConversations)]
    [InlineData(ChatOperation.ReadConversationDetails)]
    [InlineData(ChatOperation.ReadMessageHistory)]
    [InlineData(ChatOperation.SubmitMessage)]
    public async Task PositiveClaimsRemainUnavailableForEveryOperation(ChatOperation operation)
    {
        var reader = new Reader();
        var policy = new ChatConversationAccessPolicy(new User(), reader, new Clock());
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await policy.AuthorizeAsync(Request(operation)));
        Assert.Equal(operation == ChatOperation.ListConversations ? 0 : 1, reader.Calls);
    }

    [Theory]
    [InlineData("missing-m2")]
    [InlineData("missing-m5")]
    [InlineData("wrong-module")]
    [InlineData("wrong-profile")]
    [InlineData("wrong-conversation")]
    [InlineData("missing-participant")]
    [InlineData("inconsistent")]
    [InlineData("expired")]
    [InlineData("cross-request")]
    [InlineData("listing-to-send")]
    [InlineData("details-to-history")]
    [InlineData("unavailable")]
    public async Task IncompleteOrReusedEvidenceCannotGrant(string scenario)
    {
        var reader = new Reader { Scenario = scenario };
        var policy = new ChatConversationAccessPolicy(new User(), reader, new Clock());
        var request = Request(scenario == "details-to-history" ? ChatOperation.ReadMessageHistory : ChatOperation.SubmitMessage);
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await policy.AuthorizeAsync(request));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("revoked")]
    public async Task ExplicitProhibitionIsDenied(string scenario)
    {
        var policy = new ChatConversationAccessPolicy(new User(), new Reader { Scenario = scenario }, new Clock());
        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(Request()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task InvalidOperationsAreDenied(int operation)
    {
        var reader = new Reader();
        var policy = new ChatConversationAccessPolicy(new User(), reader);
        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(Request((ChatOperation)operation)));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task InvalidTargetsAreDenied()
    {
        var policy = new ChatConversationAccessPolicy(new User(), new Reader());
        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(Request() with { ConversationId = null }));
        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(Request() with { Module = 0 }));
        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(Request(ChatOperation.ListConversations) with { ConversationId = Conversation }));
    }

    [Fact]
    public async Task LegacyDefaultsNeverReadLegacyEvidenceOrGrant()
    {
        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        Assert.Null(await fallback.AuthorizeConversationAsync(ChatModule.Social, Conversation));
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await fallback.AuthorizeAsync(Request()));
        IChatConversationAccessEvidenceReader reader = new LegacyReader();
        Assert.Null(await reader.ReadOperationAsync(Subject, Request(), Subject));
        Assert.Equal(ChatAuthorizationOutcome.Unavailable,
            await new ChatConversationAccessPolicy(new User(), reader).AuthorizeAsync(Request()));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringReadPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader { Cancel = cancellation };
        var policy = new ChatConversationAccessPolicy(new User(), reader, new Clock());
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => policy.AuthorizeAsync(Request(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => policy.AuthorizeAsync(Request(), cancellation.Token));
        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fallback.AuthorizeAsync(Request(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyExceptionsExposeOnlyUnavailable(bool timeout)
    {
        var reader = new Reader { Failure = timeout ? new OperationCanceledException(Subject.ToString()) : new Exception(Profile.ToString()) };
        var policy = new ChatConversationAccessPolicy(new User(), reader);
        var outcome = await policy.AuthorizeAsync(Request());
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, outcome);
        Assert.DoesNotContain(Profile.ToString(), outcome.ToString());
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId => Subject;
        public bool IsAuthenticated => true;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2030-01-01T00:00:00Z");
    }

    private sealed class LegacyReader : IChatConversationAccessEvidenceReader
    {
        public Task<ChatConversationAccessEvidence?> ReadAsync(Guid subject, ChatModule module, Guid conversation, Guid request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Legacy reader must not be invoked.");
    }

    private sealed class Reader : IChatConversationAccessEvidenceReader
    {
        internal string Scenario { get; init; } = "";
        internal Exception? Failure { get; init; }
        internal CancellationTokenSource? Cancel { get; init; }
        internal int Calls { get; private set; }
        public Task<ChatConversationAccessEvidence?> ReadAsync(Guid subject, ChatModule module, Guid conversation, Guid request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Operation evaluation must remain independent.");

        public Task<ChatOperationAccessEvidence?> ReadOperationAsync(Guid subject, ChatAuthorizationRequest request, Guid evaluation, CancellationToken cancellationToken)
        {
            Calls++;
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Scenario == "missing-m5") return Task.FromResult<ChatOperationAccessEvidence?>(null);
            var now = new Clock().GetUtcNow();
            // Finite validity for these fixed-clock prohibition fixtures only; no production TTL.
            var validUntilUtc = Scenario is "denied" or "revoked"
                ? now.AddTicks(1)
                : DateTimeOffset.MaxValue;
            var binding = new ChatConversationAccessEvidence
            {
                RequestId = evaluation, AccountSubjectId = subject, AccountId = Account,
                ProfileAccountId = Account, ModuleProfileId = Profile, ProfileModule = request.Module,
                Conversation = ChatConversation.Create(request.Module, Conversation,
                    [ChatParticipantReference.Create(request.Module, Account, Profile)]),
                ObservedAtUtc = now, ValidUntilUtc = validUntilUtc, IsCurrent = true
            };
            binding = Scenario switch
            {
                "wrong-module" => binding with { ProfileModule = ChatModule.Learning },
                "wrong-profile" => binding with { ModuleProfileId = Subject },
                "wrong-conversation" => binding with { Conversation = ChatConversation.Create(request.Module, Subject, []) },
                "missing-participant" => binding with { Conversation = ChatConversation.Create(request.Module, Conversation, []) },
                "inconsistent" => binding with { IsCurrent = false },
                "expired" => binding with { ValidUntilUtc = now },
                "revoked" => binding with { IsRevoked = true },
                _ => binding
            };
            var outcome = Scenario switch
            {
                "denied" => ChatAuthorizationOutcome.Denied,
                "unavailable" => ChatAuthorizationOutcome.Unavailable,
                _ => ChatAuthorizationOutcome.Allowed
            };
            var evidence = new ChatOperationAccessEvidence(
                Scenario == "cross-request" ? Subject : evaluation,
                Scenario == "listing-to-send" ? Request(ChatOperation.ListConversations) :
                Scenario == "details-to-history" ? Request(ChatOperation.ReadConversationDetails) : request,
                outcome, Scenario == "missing-m2" ? null : binding);
            Assert.Equal(nameof(ChatOperationAccessEvidence), evidence.ToString());
            return Task.FromResult<ChatOperationAccessEvidence?>(evidence);
        }
    }
}
