using System.Diagnostics;
using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatConversationManagementTests
{
    private static readonly Guid SubjectA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SubjectB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SubjectC = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid AccountA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid AccountB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid AccountC = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid ProfileA = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ProfileB = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid ProfileC = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherConversation = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid Request = Guid.Parse("50000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task WorkingDemonstrationUsesRealServiceAndDevelopmentStorage(ChatModule module)
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authorityA = Authority.For(SubjectA, AccountA, ProfileA, module);
        var authorityB = Authority.For(SubjectB, AccountB, ProfileB, module);
        var authorityC = Authority.For(SubjectC, AccountC, ProfileC, module);
        var a = Service(SubjectA, repository, authorityA);
        var b = Service(SubjectB, repository, authorityB);
        var c = Service(SubjectC, repository, authorityC);
        var candidate = Candidate(module);

        var created = await a.CreateAsync(module, Request, candidate);
        Assert.Equal(ChatConversationStatus.Success, created.Status);
        var opened = await a.OpenAsync(module, Conversation);
        Assert.Same(created.Value, opened.Value);
        var details = await a.GetDetailsAsync(module, Conversation);
        Assert.Equal(2, details.Value!.Participants.Count);
        Assert.Equal(Conversation, Assert.Single((await a.ListAsync(module)).Value!).ConversationId);
        Assert.Equal(ChatConversationStatus.Success, (await b.OpenAsync(module, Conversation)).Status);
        Assert.Single((await b.ListAsync(module)).Value!);

        authorityB.Operations.Outcome = ChatAuthorizationOutcome.Denied;
        Assert.Equal(ChatConversationStatus.Denied, (await b.OpenAsync(module, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await c.OpenAsync(module, Conversation)).Status);
        Assert.Empty((await c.ListAsync(module)).Value!);

        var otherModule = module == ChatModule.Learning ? ChatModule.Hobbies : ChatModule.Learning;
        Assert.Equal(ChatConversationStatus.Unavailable, (await a.OpenAsync(otherModule, Conversation)).Status);
        Assert.Same(created.Value, (await a.CreateAsync(module, Request, candidate)).Value);
        Assert.Same(created.Value, (await a.OpenAsync(module, Conversation)).Value);
        Assert.Single((await a.ListAsync(module)).Value!);
    }

    [Fact]
    public async Task CreationHasAnIndependentNonGrantingAuthorityDefault()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var participant = ChatParticipantReference.Create(ChatModule.Social, AccountA, ProfileA);
        var authority = new ScopeOnlyAuthority(new(SubjectA, participant));
        var service = new ChatConversationManagementService(new User(SubjectA), new Operations(), repository, authority);

        Assert.Equal(ChatConversationStatus.Unavailable, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
        Assert.Empty(await repository.ListAsync(participant, default));
    }

    [Theory]
    [InlineData("no-m2")]
    [InlineData("no-m5")]
    [InlineData("unauthenticated")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-module")]
    public async Task MissingOrConflictingAuthorityNeverReadsOrCreatesData(string scenario)
    {
        using var storage = new DevelopmentChatConversationRepository();
        var repository = new ProbeRepository(storage);
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var user = new User(SubjectA) { IsAuthenticated = scenario != "unauthenticated" };
        if (scenario == "wrong-subject") authority.Scope = authority.Scope! with { VerifiedSubjectId = SubjectB };
        if (scenario == "wrong-module")
            authority.Scope = new(SubjectA, ChatParticipantReference.Create(ChatModule.Learning, AccountA, ProfileA));
        if (scenario == "no-m5") authority.Operations.Outcome = ChatAuthorizationOutcome.Unavailable;
        var service = new ChatConversationManagementService(user, authority.Operations, repository,
            scenario == "no-m2" ? null : authority);

        Assert.Equal(ChatConversationStatus.Unavailable, (await service.OpenAsync(ChatModule.Social, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.ListAsync(ChatModule.Social)).Status);
        Assert.Equal(0, repository.Reads);
        if (scenario != "no-m5")
            Assert.Equal(ChatConversationStatus.Unavailable, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    public async Task ParticipantIsolationIsEnforcedBeyondSyntheticAllowResults(string mismatch)
    {
        using var repository = new DevelopmentChatConversationRepository();
        var owner = Authority.For(SubjectA, AccountA, ProfileA);
        Assert.Equal(ChatConversationStatus.Success, (await Service(SubjectA, repository, owner).CreateAsync(ChatModule.Social, Request, Candidate())).Status);
        var module = mismatch == "module" ? ChatModule.Learning : ChatModule.Social;
        var foreign = Authority.For(SubjectA, mismatch == "account" ? AccountC : AccountA,
            mismatch == "profile" ? ProfileC : ProfileA, module);
        var service = Service(SubjectA, repository, foreign);

        Assert.Equal(ChatConversationStatus.Unavailable, (await service.GetDetailsAsync(module, Conversation)).Status);
        Assert.Empty((await service.ListAsync(module)).Value!);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("unmatched")]
    [InlineData("inactive")]
    public async Task CurrentSyntheticEligibilityIsRecheckedOnEveryOperation(string reason)
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var service = Service(SubjectA, repository, authority);
        await service.CreateAsync(ChatModule.Social, Request, Candidate());
        authority.Operations.Outcome = reason == "inactive" ? ChatAuthorizationOutcome.Unavailable : ChatAuthorizationOutcome.Denied;
        authority.Creation = ChatAuthorizationOutcome.Denied;
        authority.Item = ChatAuthorizationOutcome.Denied;
        if (reason == "inactive") authority.Scope = null;

        Assert.NotEqual(ChatConversationStatus.Success, (await service.OpenAsync(ChatModule.Social, Conversation)).Status);
        Assert.NotEqual(ChatConversationStatus.Success, (await service.ListAsync(ChatModule.Social)).Status);
        Assert.NotEqual(ChatConversationStatus.Success, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
    }

    [Fact]
    public async Task ScopeChangeDuringLookupPreventsReadDelivery()
    {
        using var storage = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        await Service(SubjectA, storage, authority).CreateAsync(ChatModule.Social, Request, Candidate());
        var repository = new ProbeRepository(storage)
        {
            AfterRead = () => authority.Scope = new(SubjectA, ChatParticipantReference.Create(ChatModule.Social, AccountC, ProfileC))
        };

        Assert.Equal(ChatConversationStatus.Unavailable,
            (await Service(SubjectA, repository, authority).OpenAsync(ChatModule.Social, Conversation)).Status);
    }

    [Fact]
    public async Task ListingNeedsItsOwnScopeAndPerConversationVisibility()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var service = Service(SubjectA, repository, authority);
        await service.CreateAsync(ChatModule.Social, Request, Candidate());
        authority.Item = ChatAuthorizationOutcome.Denied;
        Assert.Empty((await service.ListAsync(ChatModule.Social)).Value!);
        authority.Item = ChatAuthorizationOutcome.Unavailable;
        var unavailable = await service.ListAsync(ChatModule.Social);
        Assert.Equal(ChatConversationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        authority.Item = ChatAuthorizationOutcome.Allowed;
        authority.Operations.ListOutcome = ChatAuthorizationOutcome.Unavailable;
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.ListAsync(ChatModule.Social)).Status);
        Assert.Equal(ChatConversationStatus.Success, (await service.GetDetailsAsync(ChatModule.Social, Conversation)).Status);
    }

    [Fact]
    public async Task ListingRevocationAtProjectionBoundaryReleasesNoPartialPage()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var service = Service(SubjectA, repository, authority);
        await service.CreateAsync(ChatModule.Social, Request, Candidate());
        var calls = 0;
        authority.ItemDecision = () => ++calls == 1 ? ChatAuthorizationOutcome.Allowed : ChatAuthorizationOutcome.Denied;

        var result = await service.ListAsync(ChatModule.Social);
        Assert.Equal(ChatConversationStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ConcurrentEquivalentCreatesAndOpensHaveOneCanonicalConversation()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var service = Service(SubjectA, repository, authority);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await service.CreateAsync(ChatModule.Social, Request, Candidate());
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(ChatConversationStatus.Success, result.Status));
        Assert.All(results, result => Assert.Same(results[0].Value, result.Value));
        Assert.Single((await service.ListAsync(ChatModule.Social)).Value!);
        var opens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.OpenAsync(ChatModule.Social, Conversation)));
        Assert.All(opens, result => Assert.Same(results[0].Value, result.Value));
    }

    [Fact]
    public async Task IdempotencyRejectsChangedTargetsAndParticipantSets()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        var service = Service(SubjectA, repository, authority);
        await service.CreateAsync(ChatModule.Social, Request, Candidate());

        Assert.Equal(ChatConversationStatus.Conflict,
            (await service.CreateAsync(ChatModule.Social, Request, Candidate(id: OtherConversation))).Status);
        var changed = ChatConversation.Create(ChatModule.Social, Conversation,
            [ChatParticipantReference.Create(ChatModule.Social, AccountA, ProfileA)]);
        Assert.Equal(ChatConversationStatus.Conflict,
            (await service.CreateAsync(ChatModule.Social, SubjectB, changed)).Status);
        Assert.Single((await service.ListAsync(ChatModule.Social)).Value!);
        Assert.Equal(ChatConversationStatus.Success,
            (await service.CreateAsync(ChatModule.Social, SubjectB, Candidate())).Status);
    }

    [Fact]
    public async Task RequestKeysAreIsolatedByTrustedActorScope()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var a = Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA));
        var c = Service(SubjectC, repository, Authority.For(SubjectC, AccountC, ProfileC));
        await a.CreateAsync(ChatModule.Social, Request, Candidate());
        var foreign = ChatConversation.Create(ChatModule.Social, OtherConversation,
            [ChatParticipantReference.Create(ChatModule.Social, AccountC, ProfileC)]);
        Assert.Equal(ChatConversationStatus.Success, (await c.CreateAsync(ChatModule.Social, Request, foreign)).Status);
        Assert.Equal(Conversation, Assert.Single((await a.ListAsync(ChatModule.Social)).Value!).ConversationId);
        Assert.Equal(OtherConversation, Assert.Single((await c.ListAsync(ChatModule.Social)).Value!).ConversationId);
    }

    [Fact]
    public async Task CancelledRequestDoesNotCreateAndAnAmbiguousLocalCommitCanReplay()
    {
        using var storage = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = Service(SubjectA, storage, authority);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateAsync(ChatModule.Social, Request, Candidate(), cancellation.Token));
        Assert.Empty((await service.ListAsync(ChatModule.Social)).Value!);

        using var afterCommit = new CancellationTokenSource();
        var repository = new ProbeRepository(storage) { AfterCreate = () => afterCommit.Cancel() };
        var interrupted = Service(SubjectA, repository, authority);
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.CreateAsync(ChatModule.Social, Request, Candidate(), afterCommit.Token));
        Assert.Equal(ChatConversationStatus.Success, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
        Assert.Single((await service.ListAsync(ChatModule.Social)).Value!);
    }

    [Theory]
    [InlineData("find")]
    [InlineData("list")]
    [InlineData("create")]
    public async Task RepositoryFailuresReturnTypedErrorsWithoutPrivateContent(string operation)
    {
        using var storage = new DevelopmentChatConversationRepository();
        var repository = new ProbeRepository(storage) { Failure = new InvalidOperationException(AccountA + " private message") };
        var service = Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA));
        string diagnostic;
        if (operation == "list")
        {
            var result = await service.ListAsync(ChatModule.Social);
            Assert.Equal(ChatConversationStatus.Unavailable, result.Status);
            Assert.Null(result.Value);
            diagnostic = result.ToString();
        }
        else
        {
            var result = operation == "find"
                ? await service.OpenAsync(ChatModule.Social, Conversation)
                : await service.CreateAsync(ChatModule.Social, Request, Candidate());
            Assert.Equal(ChatConversationStatus.Unavailable, result.Status);
            Assert.Null(result.Value);
            diagnostic = result.ToString();
        }
        Assert.DoesNotContain(AccountA.ToString(), diagnostic);
        Assert.DoesNotContain("private message", diagnostic);
    }

    [Fact]
    public async Task InvalidSelectorsAndParticipantSetsCannotCreate()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var service = Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA));
        Assert.Equal(ChatConversationStatus.InvalidRequest, (await service.CreateAsync(ChatModule.Social, Guid.Empty, Candidate())).Status);
        Assert.Equal(ChatConversationStatus.InvalidRequest, (await service.CreateAsync(ChatModule.Social, Request,
            ChatConversation.Create(ChatModule.Social, Conversation, []))).Status);
        Assert.Equal(ChatConversationStatus.InvalidRequest, (await service.OpenAsync(ChatModule.Social, Guid.Empty)).Status);
        Assert.Equal(ChatConversationStatus.InvalidRequest, (await service.ListAsync((ChatModule)0)).Status);
        Assert.Empty((await service.ListAsync(ChatModule.Social)).Value!);
    }

    [Fact]
    public async Task MalformedRepositoryResultsCannotLeakForeignConversations()
    {
        using var storage = new DevelopmentChatConversationRepository();
        var foreign = ChatConversation.Create(ChatModule.Social, OtherConversation,
            [ChatParticipantReference.Create(ChatModule.Social, AccountC, ProfileC)]);
        var repository = new ProbeRepository(storage) { Foreign = foreign };
        var service = Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA));
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.OpenAsync(ChatModule.Social, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.ListAsync(ChatModule.Social)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
    }

    [Fact]
    public async Task ProductionDenyAllAndMissingCreationAuthorityRemainNonGranting()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var scope = new ScopeOnlyAuthority(new(SubjectA, ChatParticipantReference.Create(ChatModule.Social, AccountA, ProfileA)));
        var service = new ChatConversationManagementService(new User(SubjectA), new DenyAllChatAccessAuthorizer(), repository, scope);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.OpenAsync(ChatModule.Social, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.ListAsync(ChatModule.Social)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.CreateAsync(ChatModule.Social, Request, Candidate())).Status);
    }

    [Fact]
    public async Task StructuredDiagnosticsContainOnlyOperationAndOutcome()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Andril.Communication.Chat.Conversations",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using var repository = new DevelopmentChatConversationRepository();
        await Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA)).CreateAsync(ChatModule.Social, Request, Candidate());
        Assert.NotEmpty(captured);
        Assert.All(captured.SelectMany(activity => activity.Tags), tag => Assert.Equal("chat.outcome", tag.Key));
        Assert.DoesNotContain(AccountA.ToString(), string.Join(",", captured.SelectMany(activity => activity.Tags).Select(tag => tag.Value)));
    }

    [Theory]
    [InlineData(ChatAuthorizationOutcome.Unavailable)]
    [InlineData(ChatAuthorizationOutcome.Denied)]
    public async Task MissingOrDeniedCreationPermissionCannotWrite(
        ChatAuthorizationOutcome outcome)
    {
        using var repository = new DevelopmentChatConversationRepository();
        var authority = Authority.For(SubjectA, AccountA, ProfileA);
        authority.Creation = outcome;
        var expected = outcome == ChatAuthorizationOutcome.Denied
            ? ChatConversationStatus.Denied
            : ChatConversationStatus.Unavailable;
        var result = await Service(SubjectA, repository, authority).CreateAsync(ChatModule.Social, Request, Candidate());
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Value);
        Assert.Empty(await repository.ListAsync(authority.Scope!.Participant, default));
    }

    [Fact]
    public async Task ForeignConversationIdCollisionReturnsOnlyUnavailable()
    {
        using var repository = new DevelopmentChatConversationRepository();
        var a = Service(SubjectA, repository, Authority.For(SubjectA, AccountA, ProfileA));
        var c = Service(SubjectC, repository, Authority.For(SubjectC, AccountC, ProfileC));
        await a.CreateAsync(ChatModule.Social, Request, Candidate());
        var foreign = ChatConversation.Create(ChatModule.Social, Conversation,
            [ChatParticipantReference.Create(ChatModule.Social, AccountC, ProfileC)]);
        var result = await c.CreateAsync(ChatModule.Social, Request, foreign);
        Assert.Equal(ChatConversationStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
        Assert.Empty((await c.ListAsync(ChatModule.Social)).Value!);
        Assert.Equal(2, (await a.GetDetailsAsync(ChatModule.Social, Conversation)).Value!.Participants.Count);
    }

    [Fact]
    public async Task DefaultDevelopmentAuthoritiesPropagateCancellation()
    {
        IChatConversationDevelopmentAuthority authority = new ScopeOnlyAuthority(
            new(SubjectA, ChatParticipantReference.Create(ChatModule.Social, AccountA, ProfileA)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scope = new ChatConversationScope(SubjectA, ChatParticipantReference.Create(ChatModule.Social, AccountA, ProfileA));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => authority.AuthorizeCreationAsync(scope, Candidate(), Request, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => authority.AuthorizeListingItemAsync(scope, Candidate(), cancellation.Token));
    }
    private static ChatConversation Candidate(ChatModule module = ChatModule.Social, Guid? id = null) =>
        ChatConversation.Create(module, id ?? Conversation,
            [ChatParticipantReference.Create(module, AccountA, ProfileA), ChatParticipantReference.Create(module, AccountB, ProfileB)]);

    private static ChatConversationManagementService Service(Guid subject, IChatConversationRepository repository, Authority authority) =>
        new(new User(subject), authority.Operations, repository, authority);

    private sealed class User(Guid subject) : ICurrentUser
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated { get; init; } = true;
    }

    private sealed class Operations : IChatAccessAuthorizer
    {
        internal ChatAuthorizationOutcome Outcome { get; set; } = ChatAuthorizationOutcome.Allowed;
        internal ChatAuthorizationOutcome? ListOutcome { get; set; }
        public Task<ChatAuthorizationOutcome> AuthorizeAsync(ChatAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation == ChatOperation.ListConversations ? ListOutcome ?? Outcome : Outcome);
        }
        public Task<ChatAuthorizedActor?> AuthorizeConversationAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("D8 must not fall back to legacy grants.");
    }

    private sealed class ScopeOnlyAuthority(ChatConversationScope scope) : IChatConversationDevelopmentAuthority
    {
        public Task<ChatConversationScope?> ReadScopeAsync(Guid subject, ChatModule module, CancellationToken cancellationToken) =>
            Task.FromResult<ChatConversationScope?>(scope);
    }

    // Test-owned grants model explicit decisions only, without ratifying real M2/M5 rules.
    private sealed class Authority : IChatConversationDevelopmentAuthority
    {
        internal ChatConversationScope? Scope { get; set; }
        internal Operations Operations { get; } = new();
        internal ChatAuthorizationOutcome Creation { get; set; } = ChatAuthorizationOutcome.Allowed;
        internal ChatAuthorizationOutcome Item { get; set; } = ChatAuthorizationOutcome.Allowed;
        internal Func<ChatAuthorizationOutcome>? ItemDecision { get; set; }

        internal static Authority For(Guid subject, Guid account, Guid profile, ChatModule module = ChatModule.Social) =>
            new() { Scope = new(subject, ChatParticipantReference.Create(module, account, profile)) };

        public Task<ChatConversationScope?> ReadScopeAsync(Guid subject, ChatModule module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Scope);
        }
        public Task<ChatAuthorizationOutcome> AuthorizeCreationAsync(ChatConversationScope scope, ChatConversation candidate, Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Creation);
        }
        public Task<ChatAuthorizationOutcome> AuthorizeListingItemAsync(ChatConversationScope scope, ChatConversation conversation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ItemDecision?.Invoke() ?? Item);
        }
    }

    private sealed class ProbeRepository(IChatConversationRepository inner) : IChatConversationRepository
    {
        internal int Reads { get; private set; }
        internal Exception? Failure { get; init; }
        internal Action? AfterRead { get; init; }
        internal Action? AfterCreate { get; init; }
        internal ChatConversation? Foreign { get; init; }

        public async Task<ChatConversation?> FindAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            var value = Foreign ?? await inner.FindAsync(module, conversationId, cancellationToken);
            AfterRead?.Invoke();
            return value;
        }
        public Task<IReadOnlyList<ChatConversation>> ListAsync(ChatParticipantReference participant, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return Foreign is null ? inner.ListAsync(participant, cancellationToken)
                : Task.FromResult<IReadOnlyList<ChatConversation>>([Foreign]);
        }
        public async Task<ChatConversationResult<ChatConversation>> GetOrCreateAsync(ChatConversationScope scope, Guid requestId, ChatConversation candidate, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var value = Foreign is null ? await inner.GetOrCreateAsync(scope, requestId, candidate, cancellationToken)
                : new(ChatConversationStatus.Success, Foreign);
            AfterCreate?.Invoke();
            return value;
        }
    }
}
