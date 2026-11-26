using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

[CollectionDefinition("Chat message management", DisableParallelization = true)]
public sealed class ChatMessageManagementCollection;

// D8 diagnostics tests observe a process-wide ActivitySource. Keep these tests
// from overlapping that listener; individual concurrency scenarios still run tasks.
[Collection("Chat message management")]
public sealed class ChatMessageManagementTests
{
    private static readonly Guid ConversationId = Key(400);
    private static readonly Guid OtherConversationId = Key(401);
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task ExecutableConversationAndMessagingDemonstration(ChatModule module)
    {
        using var fixture = await Fixture.CreateAsync(module);
        Assert.Equal(ChatConversationStatus.Success, (await fixture.A.Conversations.OpenAsync(module, ConversationId)).Status);
        var sent = await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(1), "Hello B");
        Assert.Equal(ChatConversationStatus.Success, sent.Status);
        Assert.Equal(Now, sent.Value!.CreatedAtUtc);
        Assert.Equal(fixture.A.Scope.Participant.ModuleProfileId, sent.Value.Sender.ModuleProfileId);

        var received = await fixture.B.Messages.GetMessageHistoryAsync(module, ConversationId, 2);
        Assert.Same(sent.Value, Assert.Single(received.Value!.Items));
        Assert.Same(sent.Value, (await fixture.B.Messages.GetMessageAsync(module, ConversationId, sent.Value.MessageId)).Value);
        var foreign = await fixture.C.Messages.GetMessageHistoryAsync(module, ConversationId, 2);
        Assert.Equal(ChatConversationStatus.Unavailable, foreign.Status);
        Assert.Null(foreign.Value);
        var otherModule = module == ChatModule.Learning ? ChatModule.Hobbies : ChatModule.Learning;
        Assert.Equal(ChatConversationStatus.Unavailable,
            (await fixture.A.Messages.GetMessageHistoryAsync(otherModule, ConversationId, 2)).Status);

        var retry = await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(1), "Hello B");
        Assert.Same(sent.Value, retry.Value);
        for (var index = 2; index <= 5; index++)
            await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(index), $"Message {index}");
        var all = await ReadAll(fixture.B.Messages, module, 2);
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(message => message.MessageId).Distinct().Count());

        fixture.A.Permissions[ChatOperation.SubmitMessage] = ChatAuthorizationOutcome.Denied;
        fixture.B.Permissions[ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Denied;
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(6), "revoked")).Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.B.Messages.GetMessageHistoryAsync(module, ConversationId, 2)).Status);
    }

    [Fact]
    public async Task EmptyHistoryAndMissingMessageHavePrivacySafeResults()
    {
        using var fixture = await Fixture.CreateAsync();
        var page = await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10);
        Assert.Equal(ChatConversationStatus.Success, page.Status);
        Assert.Empty(page.Value!.Items);
        Assert.Null(page.Value.NextCursor);
        var missing = await fixture.A.Messages.GetMessageAsync(ChatModule.Social, ConversationId, Key(999));
        Assert.Equal(ChatConversationStatus.Unavailable, missing.Status);
        Assert.Null(missing.Value);
    }

    [Fact]
    public async Task SendAndHistoryAreIndependentOfEachOtherAndDetails()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.A.Permissions[ChatOperation.ReadConversationDetails] = ChatAuthorizationOutcome.Denied;
        fixture.A.Permissions[ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Denied;
        var sent = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "send only");
        Assert.Equal(ChatConversationStatus.Success, sent.Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2)).Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.GetMessageAsync(ChatModule.Social, ConversationId, sent.Value!.MessageId)).Status);

        fixture.A.Permissions[ChatOperation.SubmitMessage] = ChatAuthorizationOutcome.Denied;
        fixture.A.Permissions[ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Allowed;
        Assert.Equal(ChatConversationStatus.Success,
            (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2)).Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(2), "read only")).Status);
        Assert.DoesNotContain(ChatOperation.ReadConversationDetails, fixture.A.Permissions.Calls);
    }

    [Fact]
    public async Task RetryPreservesCanonicalMetadataAndExactContent()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), " hello 💬 ");
        fixture.Clock.Value = Now.AddDays(1);
        var retry = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), " hello 💬 ");
        Assert.Same(original.Value, retry.Value);
        Assert.Equal(Now, retry.Value!.CreatedAtUtc);
        Assert.Equal(" hello 💬 ", retry.Value.Text);
        var conflict = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "hello");
        Assert.Equal(ChatConversationStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Value);
        Assert.Single((await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    [Fact]
    public async Task IdempotencyKeysAreScopedToActorAndConversation()
    {
        using var fixture = await Fixture.CreateAsync();
        var a = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "A");
        var b = await fixture.B.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "B");
        Assert.NotEqual(a.Value!.MessageId, b.Value!.MessageId);
        await fixture.A.Conversations.CreateAsync(ChatModule.Social, Key(701), fixture.Candidate(OtherConversationId));
        var other = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, OtherConversationId, Key(1), "other conversation");
        Assert.Equal(ChatConversationStatus.Success, other.Status);
        Assert.Equal(2, (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items.Count);
        Assert.Single((await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, OtherConversationId, 10)).Value!.Items);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateOneCanonicalMessage()
    {
        using var fixture = await Fixture.CreateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "retry");
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(ChatConversationStatus.Success, result.Status));
        Assert.All(results, result => Assert.Same(results[0].Value, result.Value));
        Assert.Single((await ReadAll(fixture.A.Messages, ChatModule.Social, 3)));
    }

    [Fact]
    public async Task ConcurrentWritesHaveStableOrderingWithIdenticalTimestamps()
    {
        using var fixture = await Fixture.CreateAsync();
        var writes = Enumerable.Range(1, 30).Select(index => Task.Run(
            () => fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(index), $"item {index}")));
        var results = await Task.WhenAll(writes);
        Assert.All(results, result => Assert.Equal(ChatConversationStatus.Success, result.Status));
        var firstTraversal = await ReadAll(fixture.B.Messages, ChatModule.Social, 4);
        var secondTraversal = await ReadAll(fixture.B.Messages, ChatModule.Social, 7);
        Assert.Equal(30, firstTraversal.Count);
        Assert.Equal(30, firstTraversal.Select(message => message.MessageId).Distinct().Count());
        Assert.Equal(firstTraversal.Select(message => message.MessageId), secondTraversal.Select(message => message.MessageId));
        Assert.All(firstTraversal, message => Assert.Equal(Now, message.CreatedAtUtc));
    }

    [Fact]
    public async Task PaginationPinsTheInitialSnapshotAcrossLaterAppendsAndClockReversal()
    {
        using var fixture = await Fixture.CreateAsync();
        for (var index = 1; index <= 5; index++)
            await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(index), index.ToString());
        var first = (await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2)).Value!;
        Assert.Equal(new[] { "5", "4" }, first.Items.Select(message => message.Text));
        fixture.Clock.Value = Now.AddDays(-1);
        await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(6), "new");
        var second = (await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2, first.NextCursor)).Value!;
        var third = (await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2, second.NextCursor)).Value!;
        Assert.Equal(new[] { "3", "2" }, second.Items.Select(message => message.Text));
        Assert.Equal("1", Assert.Single(third.Items).Text);
        Assert.Null(third.NextCursor);
        Assert.Equal("new", (await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2)).Value!.Items[0].Text);
        Assert.Equal(second.Items.Select(message => message.MessageId),
            (await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2, first.NextCursor)).Value!.Items.Select(message => message.MessageId));
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("profile")]
    [InlineData("conversation")]
    [InlineData("page-size")]
    public async Task OpaqueCursorsCannotCrossScopesOrBeAltered(string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        for (var index = 1; index <= 3; index++)
            await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(index), index.ToString());
        var cursor = (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1)).Value!.NextCursor!;
        Assert.Equal(64, cursor.Length);
        var service = mismatch == "profile" ? fixture.B.Messages : fixture.A.Messages;
        var target = ConversationId;
        if (mismatch == "conversation")
        {
            target = OtherConversationId;
            await fixture.A.Conversations.CreateAsync(ChatModule.Social, Key(701), fixture.Candidate(target));
        }
        if (mismatch == "modified") cursor = cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A");
        var result = await service.GetMessageHistoryAsync(ChatModule.Social, target, mismatch == "page-size" ? 2 : 1, cursor);
        Assert.Equal(ChatConversationStatus.InvalidRequest, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task RevocationAppliesToReplaysCursorsAndSingleMessageReads()
    {
        using var fixture = await Fixture.CreateAsync();
        var sent = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "first");
        await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(2), "second");
        var cursor = (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1)).Value!.NextCursor;
        fixture.A.Permissions[ChatOperation.SubmitMessage] = ChatAuthorizationOutcome.Denied;
        fixture.A.Permissions[ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Denied;
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "first")).Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1, cursor)).Status);
        Assert.Equal(ChatConversationStatus.Denied,
            (await fixture.A.Messages.GetMessageAsync(ChatModule.Social, ConversationId, sent.Value!.MessageId)).Status);
    }

    [Fact]
    public async Task CancellationBeforeWriteAndAfterCommitPreservesRetrySafety()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.A.Messages.SendMessageAsync(
            ChatModule.Social, ConversationId, Key(1), "cancel", cancellationToken: cancellation.Token));
        Assert.Empty((await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);

        using var afterCommit = new CancellationTokenSource();
        var probe = new ProbeRepository(fixture.Storage) { AfterWrite = () => afterCommit.Cancel() };
        var service = new ChatMessageManagementService(fixture.A.Conversations, probe);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => service.SendMessageAsync(
            ChatModule.Social, ConversationId, Key(1), "committed", cancellationToken: afterCommit.Token));
        Assert.Equal(afterCommit.Token, error.CancellationToken);
        var retry = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "committed");
        Assert.Equal(ChatConversationStatus.Success, retry.Status);
        Assert.Single((await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("history")]
    [InlineData("get")]
    public async Task AuthorizationRevalidationPreventsDeliveryAfterStorageAccess(string operation)
    {
        using var fixture = await Fixture.CreateAsync();
        var sent = await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "private");
        void Revoke() => fixture.A.Permissions[operation == "send" ? ChatOperation.SubmitMessage : ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Denied;
        var probe = new ProbeRepository(fixture.Storage) { AfterWrite = Revoke, AfterRead = Revoke };
        var service = new ChatMessageManagementService(fixture.A.Conversations, probe);
        var status = operation switch
        {
            "send" => (await service.SendMessageAsync(ChatModule.Social, ConversationId, Key(2), "new")).Status,
            "history" => (await service.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1)).Status,
            _ => (await service.GetMessageAsync(ChatModule.Social, ConversationId, sent.Value!.MessageId)).Status
        };
        Assert.Equal(ChatConversationStatus.Denied, status);
        // Local writes may already be committed. Permission loss never implies rollback.
        fixture.A.Permissions[ChatOperation.ReadMessageHistory] = ChatAuthorizationOutcome.Allowed;
        Assert.Equal(operation == "send" ? 2 : 1,
            (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items.Count);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("dependency-cancellation")]
    [InlineData("authority")]
    public async Task FailuresReturnOnlyUnavailableWithoutMessageContent(string failure)
    {
        using var fixture = await Fixture.CreateAsync();
        var probe = new ProbeRepository(fixture.Storage)
        {
            Failure = failure == "exception" ? new InvalidOperationException("private payload") :
                failure == "dependency-cancellation" ? new OperationCanceledException("private payload") : null
        };
        if (failure == "authority") fixture.A.Permissions.Failure = new InvalidOperationException("private authority");
        var service = new ChatMessageManagementService(fixture.A.Conversations, probe);
        var sent = await service.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "private payload");
        var page = await service.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1);
        var message = await service.GetMessageAsync(ChatModule.Social, ConversationId, Key(999));
        Assert.Equal(ChatConversationStatus.Unavailable, sent.Status);
        Assert.Equal(ChatConversationStatus.Unavailable, page.Status);
        Assert.Equal(ChatConversationStatus.Unavailable, message.Status);
        Assert.Null(sent.Value);
        Assert.Null(page.Value);
        Assert.Null(message.Value);
        Assert.DoesNotContain("private", sent.ToString());
    }

    [Fact]
    public async Task MalformedStorageResultsCannotReturnForeignContent()
    {
        using var fixture = await Fixture.CreateAsync();
        var foreignConversation = ChatConversation.Create(ChatModule.Social, OtherConversationId,
            [fixture.C.Scope.Participant]);
        var foreign = ChatMessage.CreateFromServerValues(ChatModule.Social, Key(999), OtherConversationId,
            foreignConversation, fixture.C.Scope.Participant, "foreign", Now);
        var probe = new ProbeRepository(fixture.Storage) { Foreign = foreign };
        var service = new ChatMessageManagementService(fixture.A.Conversations, probe);
        Assert.Equal(ChatConversationStatus.Unavailable,
            (await service.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "foreign")).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 2)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.GetMessageAsync(ChatModule.Social, ConversationId, foreign.MessageId)).Status);
    }

    [Fact]
    public async Task DeactivationMissingAuthorityAndProductionDenyAllNeverReachMessages()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.A.Authority.Active = false;
        Assert.Equal(ChatConversationStatus.Unavailable,
            (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "inactive")).Status);
        fixture.A.Authority.Active = true;
        fixture.A.Permissions[ChatOperation.SubmitMessage] = ChatAuthorizationOutcome.Unavailable;
        Assert.Equal(ChatConversationStatus.Unavailable,
            (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "missing M5")).Status);
        var deniedConversations = new ChatConversationManagementService(fixture.A.User,
            new DenyAllChatAccessAuthorizer(), fixture.Conversations, fixture.A.Authority);
        var denied = new ChatMessageManagementService(deniedConversations, fixture.Storage);
        Assert.Equal(ChatConversationStatus.Unavailable, (await denied.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 1)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await denied.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "denied")).Status);
        Assert.Empty((await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task NonNullMoodToneUsesExistingFailClosedVocabularyRules(ChatModule module)
    {
        using var fixture = await Fixture.CreateAsync(module);
        var result = await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(1), "text", "unapproved");
        Assert.Equal(ChatConversationStatus.InvalidRequest, result.Status);
        Assert.Empty((await fixture.A.Messages.GetMessageHistoryAsync(module, ConversationId, 10)).Value!.Items);
        Assert.Equal(ChatConversationStatus.Success, (await fixture.A.Messages.SendMessageAsync(module, ConversationId, Key(1), "text")).Status);
    }

    [Theory]
    [InlineData("empty-request")]
    [InlineData("empty-text")]
    [InlineData("whitespace")]
    [InlineData("page-size")]
    [InlineData("empty-message")]
    public async Task InvalidCommandsHaveNoMessageSideEffects(string invalid)
    {
        using var fixture = await Fixture.CreateAsync();
        var status = invalid switch
        {
            "empty-request" => (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Guid.Empty, "text")).Status,
            "empty-text" => (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), null)).Status,
            "whitespace" => (await fixture.A.Messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), " ")).Status,
            "page-size" => (await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 0)).Status,
            _ => (await fixture.A.Messages.GetMessageAsync(ChatModule.Social, ConversationId, Guid.Empty)).Status
        };
        Assert.Equal(ChatConversationStatus.InvalidRequest, status);
        Assert.Empty((await fixture.A.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    [Fact]
    public async Task ServerIdCollisionFailsClosedWithoutDuplicatingState()
    {
        using var fixture = await Fixture.CreateAsync();
        using var storage = new DevelopmentChatMessageRepository(fixture.Clock, () => Key(999));
        var service = new ChatMessageManagementService(fixture.A.Conversations, storage);
        Assert.Equal(ChatConversationStatus.Success, (await service.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "first")).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await service.SendMessageAsync(ChatModule.Social, ConversationId, Key(2), "second")).Status);
        Assert.Single((await service.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    [Fact]
    public async Task RevocationDuringConversationLookupPreventsMessageInsertion()
    {
        using var fixture = await Fixture.CreateAsync();
        var lookup = new LookupProbeRepository(fixture.Conversations,
            () => fixture.A.Permissions[ChatOperation.SubmitMessage] = ChatAuthorizationOutcome.Denied);
        var conversations = new ChatConversationManagementService(fixture.A.User,
            fixture.A.Permissions, lookup, fixture.A.Authority);
        var messages = new ChatMessageManagementService(conversations, fixture.Storage);

        var result = await messages.SendMessageAsync(ChatModule.Social, ConversationId, Key(1), "revoked during lookup");

        Assert.Equal(ChatConversationStatus.Denied, result.Status);
        Assert.Null(result.Value);
        Assert.Empty((await fixture.B.Messages.GetMessageHistoryAsync(ChatModule.Social, ConversationId, 10)).Value!.Items);
    }

    private sealed class LookupProbeRepository(IChatConversationRepository inner, Action afterLookup) : IChatConversationRepository
    {
        public async Task<ChatConversation?> FindAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken)
        {
            var conversation = await inner.FindAsync(module, conversationId, cancellationToken);
            afterLookup();
            return conversation;
        }

        public Task<IReadOnlyList<ChatConversation>> ListAsync(ChatParticipantReference participant, CancellationToken cancellationToken) =>
            inner.ListAsync(participant, cancellationToken);

        public Task<ChatConversationResult<ChatConversation>> GetOrCreateAsync(ChatConversationScope scope,
            Guid requestId, ChatConversation candidate, CancellationToken cancellationToken) =>
            inner.GetOrCreateAsync(scope, requestId, candidate, cancellationToken);
    }

    private static Guid Key(int number) => new(number, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static async Task<List<ChatMessage>> ReadAll(ChatMessageManagementService service, ChatModule module, int pageSize)
    {
        var messages = new List<ChatMessage>();
        string? cursor = null;
        do
        {
            var result = await service.GetMessageHistoryAsync(module, ConversationId, pageSize, cursor);
            Assert.Equal(ChatConversationStatus.Success, result.Status);
            messages.AddRange(result.Value!.Items);
            cursor = result.Value.NextCursor;
        } while (cursor is not null);
        return messages;
    }

    private sealed class Fixture : IDisposable
    {
        internal DevelopmentChatConversationRepository Conversations { get; } = new();
        internal Clock Clock { get; } = new();
        internal DevelopmentChatMessageRepository Storage { get; }
        internal Actor A { get; }
        internal Actor B { get; }
        internal Actor C { get; }
        private int messageId = 1000;

        private Fixture(ChatModule module)
        {
            Storage = new(Clock, () => Key(Interlocked.Increment(ref messageId)));
            A = new(Key(1), Key(101), Key(201), module, Conversations, Storage);
            B = new(Key(2), Key(102), Key(202), module, Conversations, Storage);
            C = new(Key(3), Key(103), Key(203), module, Conversations, Storage);
        }

        internal static async Task<Fixture> CreateAsync(ChatModule module = ChatModule.Social)
        {
            var fixture = new Fixture(module);
            var created = await fixture.A.Conversations.CreateAsync(module, Key(700), fixture.Candidate(ConversationId));
            Assert.Equal(ChatConversationStatus.Success, created.Status);
            return fixture;
        }

        internal ChatConversation Candidate(Guid id) => ChatConversation.Create(A.Scope.Participant.Module, id,
            [A.Scope.Participant, B.Scope.Participant]);

        public void Dispose()
        {
            Storage.Dispose();
            Conversations.Dispose();
        }
    }

    private sealed class Actor
    {
        internal User User { get; }
        internal Authority Authority { get; }
        internal Permissions Permissions { get; } = new();
        internal ChatConversationScope Scope => Authority.Scope;
        internal ChatConversationManagementService Conversations { get; }
        internal ChatMessageManagementService Messages { get; }

        internal Actor(Guid subject, Guid account, Guid profile, ChatModule module,
            IChatConversationRepository conversations, IChatMessageRepository messages)
        {
            User = new(subject);
            Authority = new(new(subject, ChatParticipantReference.Create(module, account, profile)));
            Conversations = new(User, Permissions, conversations, Authority);
            Messages = new(Conversations, messages);
        }
    }

    private sealed class User(Guid subject) : ICurrentUser
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    // Explicit synthetic decisions only. Real owner permission rules remain pending.
    private sealed class Authority(ChatConversationScope scope) : IChatConversationDevelopmentAuthority
    {
        internal ChatConversationScope Scope => scope;
        internal bool Active { get; set; } = true;
        public Task<ChatConversationScope?> ReadScopeAsync(Guid subject, ChatModule module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Active ? scope : null);
        }
        public Task<ChatAuthorizationOutcome> AuthorizeCreationAsync(ChatConversationScope actor, ChatConversation candidate, Guid request, CancellationToken cancellationToken) =>
            Task.FromResult(Active ? ChatAuthorizationOutcome.Allowed : ChatAuthorizationOutcome.Unavailable);
        public Task<ChatAuthorizationOutcome> AuthorizeListingItemAsync(ChatConversationScope actor, ChatConversation conversation, CancellationToken cancellationToken) =>
            Task.FromResult(Active ? ChatAuthorizationOutcome.Allowed : ChatAuthorizationOutcome.Unavailable);
    }

    private sealed class Permissions : IChatAccessAuthorizer
    {
        private readonly Dictionary<ChatOperation, ChatAuthorizationOutcome> outcomes = [];
        internal List<ChatOperation> Calls { get; } = [];
        internal Exception? Failure { get; set; }
        internal ChatAuthorizationOutcome this[ChatOperation operation] { set => outcomes[operation] = value; }

        public Task<ChatAuthorizationOutcome> AuthorizeAsync(ChatAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            lock (Calls) { Calls.Add(request.Operation); }
            return Task.FromResult(outcomes.GetValueOrDefault(request.Operation, ChatAuthorizationOutcome.Allowed));
        }
        public Task<ChatAuthorizedActor?> AuthorizeConversationAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Legacy authorization cannot supply a message-operation grant.");
    }

    private sealed class ProbeRepository(IChatMessageRepository inner) : IChatMessageRepository
    {
        internal Action? AfterWrite { get; init; }
        internal Action? AfterRead { get; init; }
        internal Exception? Failure { get; init; }
        internal ChatMessage? Foreign { get; init; }

        public async Task<ChatConversationResult<ChatMessage>> AppendAsync(ChatConversationScope scope, ChatConversation conversation,
            Guid requestId, ChatTextMessageDraft content, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var result = Foreign is null ? await inner.AppendAsync(scope, conversation, requestId, content, cancellationToken)
                : new(ChatConversationStatus.Success, Foreign);
            AfterWrite?.Invoke();
            return result;
        }
        public async Task<ChatConversationResult<ChatMessageHistoryPage>> ReadHistoryAsync(ChatConversationScope scope,
            ChatConversation conversation, int pageSize, string? cursor, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var result = Foreign is null ? await inner.ReadHistoryAsync(scope, conversation, pageSize, cursor, cancellationToken)
                : new(ChatConversationStatus.Success, new ChatMessageHistoryPage([Foreign], null));
            AfterRead?.Invoke();
            return result;
        }
        public async Task<ChatConversationResult<ChatMessage>> FindAsync(ChatConversationScope scope,
            ChatConversation conversation, Guid messageId, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            var result = Foreign is null ? await inner.FindAsync(scope, conversation, messageId, cancellationToken)
                : new(ChatConversationStatus.Success, Foreign);
            AfterRead?.Invoke();
            return result;
        }
    }
}
