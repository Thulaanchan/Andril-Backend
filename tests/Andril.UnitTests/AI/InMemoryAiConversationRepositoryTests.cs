using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.UnitTests.AI.Fakes;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 5. Synthetic, in-memory contract tests only.
/// These do not validate PostgreSQL, Supabase or production authorization.
/// </summary>
public sealed class InMemoryAiConversationRepositoryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task Create_SavesFirstUserMessage_AndScopesMetadata(AiModule module)
    {
        var actor = Actor(module);
        var auth = new MutableAuthorization(actor);
        var clock = new ManualClock(Start);
        var repository = new InMemoryAiConversationRepository(auth, clock);

        var created = await repository.CreateWithUserMessageAsync(
            actor, Guid.NewGuid(), "Hello ANDRIL");

        Assert.NotEqual(Guid.Empty, created.ConversationId);
        Assert.Equal(actor.UserId, created.AccountId);
        Assert.Equal(actor.ModuleProfileId, created.ModuleProfileId);
        Assert.Equal(module, created.Module);
        Assert.Equal(Start, created.CreatedAtUtc);
        Assert.Equal(Start, created.LastSavedUserMessageAtUtc);
        Assert.Equal(Start.AddDays(14), created.ExpiresAtUtc);
        Assert.Equal(created, await repository.FindAsync(actor, created.ConversationId));

        var messages = await repository.ReadRecentMessagesAsync(
            actor, created.ConversationId, 10);
        var first = Assert.Single(messages);
        Assert.Equal(AiStoredMessageRole.User, first.Role);
        Assert.Equal("Hello ANDRIL", first.Text);
        Assert.Equal(created.ConversationId, first.ConversationId);
    }

    [Fact]
    public async Task MissingAuthorization_DeniesCreationAndReturnsNoHistory()
    {
        var actor = Actor(AiModule.Learning);
        var repository = new InMemoryAiConversationRepository(
            new MutableAuthorization(null), new ManualClock(Start));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Hi"));
        Assert.Empty(await repository.ListAsync(actor, 10));
        Assert.Null(await repository.FindAsync(actor, Guid.NewGuid()));
    }

    [Fact]
    public async Task MismatchedVerifiedIdentity_DeniesCreation()
    {
        var requested = Actor(AiModule.Learning);
        var other = Actor(AiModule.Learning);
        var repository = new InMemoryAiConversationRepository(
            new MutableAuthorization(other), new ManualClock(Start));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repository.CreateWithUserMessageAsync(requested, Guid.NewGuid(), "Hi"));
    }

    [Theory]
    [InlineData(0)] // Different account.
    [InlineData(1)] // Different module profile on the same account.
    [InlineData(2)] // Different module on the same account.
    public async Task OtherIdentity_CannotReadWriteOrDeleteOwnersConversation(int variant)
    {
        var owner = Actor(AiModule.Learning);
        var auth = new MutableAuthorization(owner);
        var repository = new InMemoryAiConversationRepository(auth, new ManualClock(Start));
        var created = await repository.CreateWithUserMessageAsync(
            owner, Guid.NewGuid(), "Private Learning text");

        var other = variant switch
        {
            0 => Actor(AiModule.Learning),
            1 => new AiAuthorizedActor(owner.UserId, Guid.NewGuid(), AiModule.Learning),
            _ => new AiAuthorizedActor(owner.UserId, Guid.NewGuid(), AiModule.Hobbies)
        };
        auth.Current = other;

        Assert.Null(await repository.FindAsync(other, created.ConversationId));
        Assert.Empty(await repository.ListAsync(other, 10));
        Assert.Empty(await repository.ReadRecentMessagesAsync(other, created.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendUserMessageAsync(
                other, created.ConversationId, Guid.NewGuid(), "Intrusion"));
        await repository.DeleteAsync(other, created.ConversationId);

        auth.Current = owner;
        Assert.NotNull(await repository.FindAsync(owner, created.ConversationId));
        Assert.Single(await repository.ReadRecentMessagesAsync(owner, created.ConversationId, 10));
    }

    [Fact]
    public async Task OneAccount_ListsOnlyItsCurrentModuleProfileConversations()
    {
        var accountId = Guid.NewGuid();
        var learning = new AiAuthorizedActor(accountId, Guid.NewGuid(), AiModule.Learning);
        var hobbies = new AiAuthorizedActor(accountId, Guid.NewGuid(), AiModule.Hobbies);
        var auth = new MutableAuthorization(learning);
        var repository = new InMemoryAiConversationRepository(auth, new ManualClock(Start));
        var learningChat = await repository.CreateWithUserMessageAsync(
            learning, Guid.NewGuid(), "Study");

        auth.Current = hobbies;
        var hobbiesChat = await repository.CreateWithUserMessageAsync(
            hobbies, Guid.NewGuid(), "Paint");
        Assert.Equal(hobbiesChat.ConversationId,
            Assert.Single(await repository.ListAsync(hobbies, 10)).ConversationId);

        auth.Current = learning;
        Assert.Equal(learningChat.ConversationId,
            Assert.Single(await repository.ListAsync(learning, 10)).ConversationId);
    }

    [Fact]
    public async Task Revocation_BlocksReadsWritesAndDeletionThroughActorContract()
    {
        var actor = Actor(AiModule.Social);
        var auth = new MutableAuthorization(actor);
        var repository = new InMemoryAiConversationRepository(auth, new ManualClock(Start));
        var conversation = await repository.CreateWithUserMessageAsync(
            actor, Guid.NewGuid(), "Fictional conversation");
        auth.Current = null;

        Assert.Null(await repository.FindAsync(actor, conversation.ConversationId));
        Assert.Empty(await repository.ListAsync(actor, 10));
        Assert.Empty(await repository.ReadRecentMessagesAsync(actor, conversation.ConversationId, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repository.AppendUserMessageAsync(actor, conversation.ConversationId,
                Guid.NewGuid(), "Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            repository.DeleteAsync(actor, conversation.ConversationId));
    }

    [Fact]
    public async Task CreateRetry_SameOperationReturnsSameChatWithoutNewMessageOrExpiry()
    {
        var actor = Actor(AiModule.Learning);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var operation = Guid.NewGuid();
        var first = await repository.CreateWithUserMessageAsync(actor, operation, "Hello");
        clock.Advance(TimeSpan.FromHours(1));
        var retry = await repository.CreateWithUserMessageAsync(actor, operation, "Hello");

        Assert.Equal(first.ConversationId, retry.ConversationId);
        Assert.Equal(first.ExpiresAtUtc, retry.ExpiresAtUtc);
        Assert.Single(await repository.ListAsync(actor, 10));
        Assert.Single(await repository.ReadRecentMessagesAsync(actor, first.ConversationId, 10));
    }

    [Fact]
    public async Task CreateRetry_WithDifferentText_IsRejected()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var operation = Guid.NewGuid();
        await repository.CreateWithUserMessageAsync(actor, operation, "Original");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateWithUserMessageAsync(actor, operation, "Changed"));
        Assert.Single(await repository.ListAsync(actor, 10));
    }

    [Fact]
    public async Task NewUserMessage_RenewsExpiryFromItsSuccessfulSave()
    {
        var actor = Actor(AiModule.Hobbies);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var created = await repository.CreateWithUserMessageAsync(
            actor, Guid.NewGuid(), "First");
        clock.Advance(TimeSpan.FromDays(13));
        var saved = await repository.AppendUserMessageAsync(
            actor, created.ConversationId, Guid.NewGuid(), "Second");
        var updated = await repository.FindAsync(actor, created.ConversationId);

        Assert.NotNull(updated);
        Assert.Equal(saved.SavedAtUtc, updated.LastSavedUserMessageAtUtc);
        Assert.Equal(Start.AddDays(27), updated.ExpiresAtUtc);
        clock.Advance(TimeSpan.FromDays(1)); // The original deadline.
        Assert.NotNull(await repository.FindAsync(actor, created.ConversationId));
        clock.Advance(TimeSpan.FromDays(13)); // The renewed deadline.
        Assert.Null(await repository.FindAsync(actor, created.ConversationId));
    }

    [Fact]
    public async Task AppendUserRetry_DoesNotDuplicateOrRenewExpiry()
    {
        var actor = Actor(AiModule.Learning);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var created = await repository.CreateWithUserMessageAsync(
            actor, Guid.NewGuid(), "First");
        clock.Advance(TimeSpan.FromHours(2));
        var operation = Guid.NewGuid();
        var first = await repository.AppendUserMessageAsync(
            actor, created.ConversationId, operation, "Second");
        var expiry = (await repository.FindAsync(actor, created.ConversationId))!.ExpiresAtUtc;
        clock.Advance(TimeSpan.FromHours(4));
        var retry = await repository.AppendUserMessageAsync(
            actor, created.ConversationId, operation, "Second");

        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(expiry,
            (await repository.FindAsync(actor, created.ConversationId))!.ExpiresAtUtc);
        Assert.Equal(2, (await repository.ReadRecentMessagesAsync(
            actor, created.ConversationId, 10)).Count);
    }

    [Fact]
    public async Task AppendUserRetry_WithDifferentText_IsRejected()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "First");
        var operation = Guid.NewGuid();
        await repository.AppendUserMessageAsync(actor, chat.ConversationId, operation, "Second");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendUserMessageAsync(actor, chat.ConversationId, operation, "Changed"));
        Assert.Equal(2, (await repository.ReadRecentMessagesAsync(
            actor, chat.ConversationId, 10)).Count);
    }

    [Fact]
    public async Task AssistantReply_RequiresAcceptedOperation()
    {
        var actor = Actor(AiModule.Hobbies);
        var repository = Repository(actor);
        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Hi");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendAssistantMessageAsync(
                actor, chat.ConversationId, Guid.NewGuid(), "Unsolicited"));
    }

    [Fact]
    public async Task AssistantReply_IsIdempotentAndDoesNotRenewExpiry()
    {
        var actor = Actor(AiModule.Learning);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var operation = Guid.NewGuid();
        var chat = await repository.CreateWithUserMessageAsync(actor, operation, "Question");
        clock.Advance(TimeSpan.FromDays(1));
        var first = await repository.AppendAssistantMessageAsync(
            actor, chat.ConversationId, operation, "Answer");
        clock.Advance(TimeSpan.FromHours(1));
        var retry = await repository.AppendAssistantMessageAsync(
            actor, chat.ConversationId, operation, "Answer");

        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(Start.AddDays(14),
            (await repository.FindAsync(actor, chat.ConversationId))!.ExpiresAtUtc);
        Assert.Equal(2, (await repository.ReadRecentMessagesAsync(
            actor, chat.ConversationId, 10)).Count);
    }

    [Fact]
    public async Task ReadRecentMessages_ReturnsBoundedChronologicalTail()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var firstOperation = Guid.NewGuid();
        var chat = await repository.CreateWithUserMessageAsync(actor, firstOperation, "Q1");
        await repository.AppendAssistantMessageAsync(actor, chat.ConversationId, firstOperation, "A1");
        var secondOperation = Guid.NewGuid();
        await repository.AppendUserMessageAsync(actor, chat.ConversationId, secondOperation, "Q2");
        await repository.AppendAssistantMessageAsync(actor, chat.ConversationId, secondOperation, "A2");

        var lastTwo = await repository.ReadRecentMessagesAsync(actor, chat.ConversationId, 2);
        Assert.Equal(new[] { "Q2", "A2" }, lastTwo.Select(m => m.Text));
        Assert.Equal(new[] { AiStoredMessageRole.User, AiStoredMessageRole.Assistant },
            lastTwo.Select(m => m.Role));
    }

    [Fact]
    public async Task ReadingHistory_DoesNotExtendInactivityWindow()
    {
        var actor = Actor(AiModule.Learning);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Hi");
        clock.Advance(TimeSpan.FromDays(13));

        Assert.NotNull(await repository.FindAsync(actor, chat.ConversationId));
        Assert.Single(await repository.ListAsync(actor, 10));
        Assert.Single(await repository.ReadRecentMessagesAsync(actor, chat.ConversationId, 10));
        Assert.Equal(Start.AddDays(14),
            (await repository.FindAsync(actor, chat.ConversationId))!.ExpiresAtUtc);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await repository.FindAsync(actor, chat.ConversationId));
    }

    [Fact]
    public async Task ExpiryIsInclusive_ReadsAndWritesDeniedAtExactDeadline()
    {
        var actor = Actor(AiModule.Social);
        var clock = new ManualClock(Start);
        var repository = Repository(actor, clock);
        var operation = Guid.NewGuid();
        var chat = await repository.CreateWithUserMessageAsync(actor, operation, "Hi");

        clock.Advance(TimeSpan.FromDays(14).Subtract(TimeSpan.FromTicks(1)));
        Assert.NotNull(await repository.FindAsync(actor, chat.ConversationId));
        clock.Advance(TimeSpan.FromTicks(1));

        Assert.Null(await repository.FindAsync(actor, chat.ConversationId));
        Assert.Empty(await repository.ListAsync(actor, 10));
        Assert.Empty(await repository.ReadRecentMessagesAsync(actor, chat.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendUserMessageAsync(actor, chat.ConversationId, Guid.NewGuid(), "Late"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendAssistantMessageAsync(actor, chat.ConversationId, operation, "Late"));
    }

    [Fact]
    public async Task ManualDelete_ImmediatelyRevokesAccess_AndRetryCannotResurrect()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var operation = Guid.NewGuid();
        var chat = await repository.CreateWithUserMessageAsync(actor, operation, "Private");
        await repository.AppendAssistantMessageAsync(actor, chat.ConversationId, operation, "Reply");

        await repository.DeleteAsync(actor, chat.ConversationId);
        await repository.DeleteAsync(actor, chat.ConversationId); // Idempotent.

        Assert.Null(await repository.FindAsync(actor, chat.ConversationId));
        Assert.Empty(await repository.ListAsync(actor, 10));
        Assert.Empty(await repository.ReadRecentMessagesAsync(actor, chat.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateWithUserMessageAsync(actor, operation, "Private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendUserMessageAsync(actor, chat.ConversationId, Guid.NewGuid(), "Resurrect"));
    }

    [Fact]
    public async Task Cancellation_BeforeOperation_LeavesConversationUntouched()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Original");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.AppendUserMessageAsync(actor, chat.ConversationId,
                Guid.NewGuid(), "Cancelled", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.DeleteAsync(actor, chat.ConversationId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.FindAsync(actor, chat.ConversationId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Cancelled", cts.Token));

        Assert.NotNull(await repository.FindAsync(actor, chat.ConversationId));
        Assert.Single(await repository.ReadRecentMessagesAsync(actor, chat.ConversationId, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ReadLimits_OutOfRangeAreRejected(int limit)
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Hi");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.ListAsync(actor, limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.ReadRecentMessagesAsync(actor, chat.ConversationId, limit));
    }

    [Fact]
    public async Task InvalidTextAndOperationIds_AreRejected()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.CreateWithUserMessageAsync(actor, Guid.Empty, "Hi"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), " \t "));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), new string('x', 501)));

        var chat = await repository.CreateWithUserMessageAsync(actor, Guid.NewGuid(), "Hi");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AppendUserMessageAsync(actor, chat.ConversationId, Guid.Empty, "Hi"));
    }

    [Fact]
    public async Task ConcurrentDuplicateCreate_ProducesOneConversation()
    {
        var actor = Actor(AiModule.Learning);
        var repository = Repository(actor);
        var operation = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 12).Select(_ =>
            Task.Run(async () => await repository.CreateWithUserMessageAsync(
                actor, operation, "Same request")));

        var results = await Task.WhenAll(attempts);
        Assert.Single(results.Select(c => c.ConversationId).Distinct());
        Assert.Single(await repository.ListAsync(actor, 10));
        Assert.Single(await repository.ReadRecentMessagesAsync(actor, results[0].ConversationId, 10));
    }

    private static AiAuthorizedActor Actor(AiModule module) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static InMemoryAiConversationRepository Repository(
        AiAuthorizedActor actor, TimeProvider? clock = null) =>
        new(new MutableAuthorization(actor), clock ?? new ManualClock(Start));

    private sealed class MutableAuthorization(AiAuthorizedActor? current)
        : IAiActorAuthorization
    {
        public AiAuthorizedActor? Current { get; set; } = current;

        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
            AiModule module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            _now = _now.Add(duration);
        }
    }
}
