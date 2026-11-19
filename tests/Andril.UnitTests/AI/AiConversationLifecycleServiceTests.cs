using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;
using Andril.UnitTests.AI.Fakes;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 7: synthetic-data lifecycle integration tests.
/// No HTTP endpoint, real profiles, database or live AI provider is used.
/// </summary>
public sealed class AiConversationLifecycleServiceTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task Start_CreatesPrivateConversationAndSavesTrimmedUserMessage(
        AiModule module)
    {
        var test = new Fixture(module);
        var conversation = await test.StartAsync("  Hello ANDRIL  ");

        Assert.Equal(test.Actor.UserId, conversation.AccountId);
        Assert.Equal(test.Actor.ModuleProfileId, conversation.ModuleProfileId);
        Assert.Equal(module, conversation.Module);
        Assert.Equal(StartUtc, conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(StartUtc.AddDays(14), conversation.ExpiresAtUtc);

        var found = await test.Service.FindConversationAsync(
            module, conversation.ConversationId);
        Assert.Equal(conversation.ConversationId, found?.ConversationId);
        Assert.Single(await test.Service.ListConversationsAsync(module, 10));
        var message = Assert.Single(await test.Service.ReadRecentMessagesAsync(
            module, conversation.ConversationId, 10));
        Assert.Equal(AiStoredMessageRole.User, message.Role);
        Assert.Equal("Hello ANDRIL", message.Text);
    }

    [Fact]
    public async Task MissingAuthorization_RejectsStartWithoutSavingAnything()
    {
        var test = new Fixture();
        test.Authorization.Current = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.StartAsync("No access"));
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Theory]
    [InlineData(0)] // Different account
    [InlineData(1)] // Different profile
    [InlineData(2)] // Wrong module
    public async Task MismatchedVerifiedActor_CannotStartConversation(int variant)
    {
        var test = new Fixture();
        var owner = test.Actor;
        var other = variant switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies }
        };

        // The service's initial authorization resolves the owner, while
        // the repository's independent recheck sees a different identity.
        var initialAuthorization = new MutableAuthorization(owner);
        test.Authorization.Current = other;
        var service = new AiConversationLifecycleService(
            initialAuthorization, test.Repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.StartConversationAsync(
                owner.Module, Guid.NewGuid(), "Blocked"));

        // Verify no conversation was written under either identity.
        Assert.Empty(await test.Repository.ListAsync(other, 10));
        test.Authorization.Current = owner;
        Assert.Empty(await test.Repository.ListAsync(owner, 10));
    }

    [Theory]
    [InlineData(0)] // Empty account ID
    [InlineData(1)] // Empty profile ID
    [InlineData(2)] // Wrong module returned by initial authorization
    public async Task InvalidInitialVerifiedActor_CannotStartConversation(int variant)
    {
        var test = new Fixture();
        test.Authorization.Current = variant switch
        {
            0 => test.Actor with { UserId = Guid.Empty },
            1 => test.Actor with { ModuleProfileId = Guid.Empty },
            _ => test.Actor with { Module = AiModule.Hobbies }
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.StartAsync("Blocked"));

        test.Authorization.Current = test.Actor;
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Fact]
    public async Task UnsupportedModule_IsRejected()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.StartConversationAsync((AiModule)99, Guid.NewGuid(), "Hello"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task EmptyUserMessage_IsRejected(string? text)
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.StartAsync(text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exactly500TextElements_AreAccepted(bool emoji)
    {
        var test = new Fixture();
        var input = emoji
            ? string.Concat(Enumerable.Repeat("😀", 500))
            : new string('a', 500);
        var conversation = await test.StartAsync(input);
        Assert.NotEqual(Guid.Empty, conversation.ConversationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreThan500TextElements_AreRejected(bool emoji)
    {
        var test = new Fixture();
        var input = emoji
            ? string.Concat(Enumerable.Repeat("😀", 501))
            : new string('a', 501);
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.StartAsync(input));
    }

    [Fact]
    public async Task MissingOperationId_IsRejected()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.Empty, "Hello"));
        var conversation = await test.StartAsync("First");
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.Empty, "Second"));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, Guid.Empty, "Reply"));
    }

    [Fact]
    public async Task MissingConversationId_IsRejectedByEveryApplicableMethod()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.FindConversationAsync(test.Actor.Module, Guid.Empty));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                Guid.Empty, Guid.NewGuid(), "Hello"));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                Guid.Empty, Guid.NewGuid(), "Reply"));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module, Guid.Empty, 10));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.DeleteConversationAsync(test.Actor.Module, Guid.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ReadLimitsOutsideTechnicalBound_AreRejected(int limit)
    {
        var test = new Fixture();
        var conversation = await test.StartAsync("First");
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ListConversationsAsync(test.Actor.Module, limit));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module,
                conversation.ConversationId, limit));
    }

    [Fact]
    public async Task ContinueAndAssistantReply_PersistInChronologicalOrder()
    {
        var test = new Fixture();
        var firstOperation = Guid.NewGuid();
        var conversation = await test.StartAsync("First", firstOperation);
        test.Clock.Advance(TimeSpan.FromHours(2));
        var secondOperation = Guid.NewGuid();
        var userReply = await test.Service.ContinueConversationAsync(
            test.Actor.Module, conversation.ConversationId,
            secondOperation, "  Second  ");
        var assistantReply = await test.Service.SaveAssistantReplyAsync(
            test.Actor.Module, conversation.ConversationId,
            secondOperation, "Assistant response");

        Assert.Equal("Second", userReply.Text);
        Assert.Equal(AiStoredMessageRole.Assistant, assistantReply.Role);
        var messages = await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10);
        Assert.Equal(new[] { "First", "Second", "Assistant response" },
            messages.Select(m => m.Text));
        var updated = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        Assert.Equal(StartUtc.AddHours(2).AddDays(14), updated!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task EmptyAssistantReply_IsRejected(string? text)
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("First", operation);
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, operation, text));
    }

    [Fact]
    public async Task UnmatchedAssistantOperation_IsRejected()
    {
        var test = new Fixture();
        var conversation = await test.StartAsync("First");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Orphan"));
    }

    [Fact]
    public async Task CreateRetry_IsIdempotentAndDoesNotRenewExpiry()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var first = await test.StartAsync("First", operation);
        test.Clock.Advance(TimeSpan.FromHours(3));
        var retry = await test.StartAsync("First", operation);

        Assert.Equal(first.ConversationId, retry.ConversationId);
        Assert.Equal(first.ExpiresAtUtc, retry.ExpiresAtUtc);
        Assert.Single(await test.Service.ListConversationsAsync(test.Actor.Module, 10));
        Assert.Single(await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, first.ConversationId, 10));
    }

    [Fact]
    public async Task CreateRetryWithDifferentText_IsRejected()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        await test.StartAsync("First", operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.StartAsync("Changed", operation));
    }

    [Fact]
    public async Task ContinueRetry_IsIdempotentAndDoesNotRenewExpiry()
    {
        var test = new Fixture();
        var conversation = await test.StartAsync("First");
        test.Clock.Advance(TimeSpan.FromDays(1));
        var operation = Guid.NewGuid();
        var first = await test.Service.ContinueConversationAsync(
            test.Actor.Module, conversation.ConversationId, operation, "Second");
        var before = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        test.Clock.Advance(TimeSpan.FromDays(1));
        var retry = await test.Service.ContinueConversationAsync(
            test.Actor.Module, conversation.ConversationId, operation, "Second");
        var after = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);

        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(before!.ExpiresAtUtc, after!.ExpiresAtUtc);
        Assert.Equal(2, (await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10)).Count);
    }

    [Fact]
    public async Task AssistantRetry_IsIdempotentAndDoesNotRenewExpiry()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("First", operation);
        var before = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        test.Clock.Advance(TimeSpan.FromHours(2));
        var first = await test.Service.SaveAssistantReplyAsync(
            test.Actor.Module, conversation.ConversationId, operation, "Reply");
        var retry = await test.Service.SaveAssistantReplyAsync(
            test.Actor.Module, conversation.ConversationId, operation, "Reply");
        var after = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);

        Assert.Equal(first.MessageId, retry.MessageId);
        Assert.Equal(before!.ExpiresAtUtc, after!.ExpiresAtUtc);
        Assert.Equal(2, (await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10)).Count);
    }

    [Fact]
    public async Task ReadingAndResuming_DoNotRenewExpiry()
    {
        var test = new Fixture();
        var conversation = await test.StartAsync("First");
        test.Clock.Advance(TimeSpan.FromDays(10));
        Assert.NotNull(await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId));
        Assert.Single(await test.Service.ListConversationsAsync(test.Actor.Module, 10));
        Assert.Single(await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10));
        var after = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        Assert.Equal(StartUtc.AddDays(14), after!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ExactlyAtFourteenDayDeadline_ConversationBecomesUnavailable()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("First", operation);
        test.Clock.Advance(TimeSpan.FromDays(14).Subtract(TimeSpan.FromTicks(1)));
        Assert.NotNull(await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId));
        test.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId));
        Assert.Empty(await test.Service.ListConversationsAsync(test.Actor.Module, 10));
        Assert.Empty(await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Late"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, operation, "Late reply"));
    }

    [Fact]
    public async Task NewUserMessageBeforeDeadline_RenewsExpiry()
    {
        var test = new Fixture();
        var conversation = await test.StartAsync("First");
        test.Clock.Advance(TimeSpan.FromDays(13));
        await test.Service.ContinueConversationAsync(test.Actor.Module,
            conversation.ConversationId, Guid.NewGuid(), "Second");
        var renewed = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        Assert.Equal(StartUtc.AddDays(27), renewed!.ExpiresAtUtc);
        test.Clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId));
        test.Clock.Advance(TimeSpan.FromDays(13));
        Assert.Null(await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId));
    }

    [Theory]
    [InlineData(0)] // Different account
    [InlineData(1)] // Different profile
    [InlineData(2)] // Different module
    public async Task AnotherIdentity_CannotReadOrAlterOwnersConversation(int variant)
    {
        var test = new Fixture();
        var owner = test.Actor;
        var conversation = await test.StartAsync("Private Learning text");
        var other = variant switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies, ModuleProfileId = Guid.NewGuid() }
        };
        test.Authorization.Current = other;

        Assert.Null(await test.Service.FindConversationAsync(other.Module,
            conversation.ConversationId));
        Assert.Empty(await test.Service.ListConversationsAsync(other.Module, 10));
        Assert.Empty(await test.Service.ReadRecentMessagesAsync(other.Module,
            conversation.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(other.Module,
                conversation.ConversationId, Guid.NewGuid(), "Intrusion"));
        await test.Service.DeleteConversationAsync(other.Module, conversation.ConversationId);

        test.Authorization.Current = owner;
        Assert.NotNull(await test.Service.FindConversationAsync(owner.Module,
            conversation.ConversationId));
    }

    [Fact]
    public async Task RevokedAuthorization_BlocksEveryLifecycleOperation()
    {
        var test = new Fixture(AiModule.Social);
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("Fictional content", operation);
        test.Authorization.Current = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.StartAsync("Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.FindConversationAsync(test.Actor.Module,
                conversation.ConversationId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.ListConversationsAsync(test.Actor.Module, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module,
                conversation.ConversationId, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, operation, "Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.DeleteConversationAsync(test.Actor.Module,
                conversation.ConversationId));
    }

    [Fact]
    public async Task Delete_IsIdempotentAndPreventsRetryResurrection()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("Private", operation);
        await test.Service.DeleteConversationAsync(test.Actor.Module,
            conversation.ConversationId);
        await test.Service.DeleteConversationAsync(test.Actor.Module,
            conversation.ConversationId);

        Assert.Null(await test.Service.FindConversationAsync(test.Actor.Module,
            conversation.ConversationId));
        Assert.Empty(await test.Service.ListConversationsAsync(test.Actor.Module, 10));
        Assert.Empty(await test.Service.ReadRecentMessagesAsync(test.Actor.Module,
            conversation.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.StartAsync("Private", operation));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Resurrect"));
    }

    [Fact]
    public async Task PreCancelledToken_StopsAllMethodsWithoutWrites()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.StartAsync("First", operation);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module,
                Guid.NewGuid(), "Cancelled", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Cancelled", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, operation, "Cancelled", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.FindConversationAsync(test.Actor.Module,
                conversation.ConversationId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.ListConversationsAsync(test.Actor.Module, 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module,
                conversation.ConversationId, 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.DeleteConversationAsync(test.Actor.Module,
                conversation.ConversationId, cts.Token));

        Assert.Single(await test.Service.ReadRecentMessagesAsync(test.Actor.Module,
            conversation.ConversationId, 10));
    }

    private sealed class Fixture
    {
        public AiAuthorizedActor Actor { get; }
        public MutableAuthorization Authorization { get; }
        public ManualClock Clock { get; }
        public InMemoryAiConversationRepository Repository { get; }
        public AiConversationLifecycleService Service { get; }

        public Fixture(AiModule module = AiModule.Learning)
        {
            Actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
            Authorization = new MutableAuthorization(Actor);
            Clock = new ManualClock(StartUtc);
            Repository = new InMemoryAiConversationRepository(Authorization, Clock);
            Service = new AiConversationLifecycleService(Authorization, Repository);
        }

        public Task<AiStoredConversation> StartAsync(
            string? text, Guid? operationId = null) =>
            Service.StartConversationAsync(Actor.Module,
                operationId ?? Guid.NewGuid(), text);
    }

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

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval)
        {
            if (interval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            _now = _now.Add(interval);
        }
    }
}
