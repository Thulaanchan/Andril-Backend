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
    public async Task SeededConversation_PreservesPrivateScopeAndHistory(
        AiModule module)
    {
        var test = new Fixture(module);
        var conversation = await test.SeedAsync("Hello ANDRIL");

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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), "No access"));
        Assert.Equal(0, test.Recording.Calls);
        Assert.Equal(0, test.Authorization.Calls);
        test.Authorization.Current = test.Actor;
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

        // Neither initial nor repository authorization can enable the disabled creation method.
        var initialAuthorization = new MutableAuthorization(owner);
        test.Authorization.Current = other;
        var service = new AiConversationLifecycleService(
            initialAuthorization, test.Repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartConversationAsync(
                owner.Module, Guid.NewGuid(), "Blocked"));
        Assert.Equal(0, initialAuthorization.Calls);

        // Verify no conversation was written under either identity.
        Assert.Empty(await test.Repository.ListAsync(other, 10));
        test.Authorization.Current = owner;
        Assert.Empty(await test.Repository.ListAsync(owner, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RepositoryReauthorizationStillProtectsReadsAfterInitialServiceAuthorization(int variant)
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("Owner history");
        var initial = new MutableAuthorization(test.Actor);
        var service = new AiConversationLifecycleService(initial, test.Repository);
        test.Authorization.Current = variant switch
        {
            0 => test.Actor with { UserId = Guid.NewGuid() },
            1 => test.Actor with { ModuleProfileId = Guid.NewGuid() },
            _ => test.Actor with { Module = AiModule.Hobbies }
        };
        Assert.Null(await service.FindConversationAsync(test.Actor.Module, conversation.ConversationId));
        Assert.Empty(await service.ListConversationsAsync(test.Actor.Module, 10));
        Assert.Empty(await service.ReadRecentMessagesAsync(test.Actor.Module, conversation.ConversationId, 10));
        Assert.Equal(3, initial.Calls);
        test.Authorization.Current = test.Actor;
        Assert.Equal(conversation, await service.FindConversationAsync(test.Actor.Module, conversation.ConversationId));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), "Blocked"));
        Assert.Equal(0, test.Recording.Calls);
        test.Authorization.Current = test.Actor;
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Fact]
    public async Task UnsupportedModule_IsRejected()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync((AiModule)99, Guid.NewGuid(), "Hello"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task EmptyUserMessage_IsRejected(string? text)
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), text));
        Assert.Equal(0, test.Recording.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exactly500TextElements_CannotEnableLegacyCreation(bool emoji)
    {
        var test = new Fixture();
        var input = emoji ? string.Concat(Enumerable.Repeat("😀", 500)) : new string('a', 500);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), input));
        Assert.Equal(0, test.Recording.Calls);
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreThan500TextElements_AreRejected(bool emoji)
    {
        var test = new Fixture();
        var input = emoji ? string.Concat(Enumerable.Repeat("😀", 501)) : new string('a', 501);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), input));
        Assert.Equal(0, test.Recording.Calls);
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Fact]
    public async Task MissingOperationId_IsRejected()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.Empty, "Hello"));
        var conversation = await test.SeedAsync("First");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.Empty, "Second"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, Guid.Empty, "Reply"));
    }

    [Fact]
    public async Task MissingConversationId_IsRejectedByEveryApplicableMethod()
    {
        var test = new Fixture();
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.FindConversationAsync(test.Actor.Module, Guid.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                Guid.Empty, Guid.NewGuid(), "Hello"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
        var conversation = await test.SeedAsync("First");
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ListConversationsAsync(test.Actor.Module, limit));
        await Assert.ThrowsAsync<AiChatValidationException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module,
                conversation.ConversationId, limit));
    }

    [Fact]
    public async Task ContinueAndSeededAssistantHistory_RemainChronological()
    {
        var test = new Fixture();
        var firstOperation = Guid.NewGuid();
        var conversation = await test.SeedAsync("First", firstOperation);
        test.Clock.Advance(TimeSpan.FromHours(2));
        var secondOperation = Guid.NewGuid();
        // Test-only fixture setup: production continuation remains disabled.
        var userReply = await test.Repository.AppendUserMessageAsync(
            test.Actor, conversation.ConversationId, secondOperation, "Second");
        // Test-only arrangement for read-policy coverage; never use the disabled service to seed.
        var assistantReply = await test.Repository.AppendAssistantMessageAsync(
            test.Actor, conversation.ConversationId, secondOperation, "Assistant response");

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
        var conversation = await test.SeedAsync("First", operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, operation, text));
    }

    [Fact]
    public async Task UnmatchedAssistantOperation_IsRejected()
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Orphan"));
    }

    [Fact]
    public async Task RepeatedLegacyCreation_CannotInsertConversationOrFirstMessage()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        for (var attempt = 0; attempt < 3; attempt++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                test.Service.StartConversationAsync(test.Actor.Module, operation, "First"));
        Assert.Equal(0, test.Recording.CreateCalls);
        Assert.Equal(0, test.Recording.Calls);
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Fact]
    public async Task CreateRetryWithDifferentText_IsRejected()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        foreach (var text in new[] { "First", "Changed" })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                test.Service.StartConversationAsync(test.Actor.Module, operation, text));
        Assert.Equal(0, test.Recording.CreateCalls);
        Assert.Empty(await test.Repository.ListAsync(test.Actor, 10));
    }

    [Fact]
    public async Task RepeatedLegacyContinuation_CannotInsertOrRenewExpiry()
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
        test.Clock.Advance(TimeSpan.FromDays(1));
        var operation = Guid.NewGuid();
        foreach (var text in new[] { "Second", "Second", "Changed" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.ContinueConversationAsync(
                test.Actor.Module, conversation.ConversationId, operation, text));
        Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
        Assert.Single(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        Assert.Equal(0, test.Recording.UserAppendCalls);
        Assert.Equal(0, test.Recording.Calls);
    }

    [Fact]
    public async Task RepeatedLegacyAssistantCalls_AreDeniedWithoutMessagesOrExpiryRenewal()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("First", operation);
        var before = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);
        test.Clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                test.Service.SaveAssistantReplyAsync(test.Actor.Module, conversation.ConversationId, operation, "Reply"));
        var after = await test.Service.FindConversationAsync(
            test.Actor.Module, conversation.ConversationId);

        Assert.Equal(before, after);
        Assert.Single(await test.Service.ReadRecentMessagesAsync(
            test.Actor.Module, conversation.ConversationId, 10));
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
    }

    [Fact]
    public async Task ReadingAndResuming_DoNotRenewExpiry()
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
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
        var conversation = await test.SeedAsync("First", operation);
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
    public async Task SeededSavedUserMessage_RenewsExpiryWhileLegacyContinuationStaysDisabled()
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
        test.Clock.Advance(TimeSpan.FromDays(13));
        // Test-only saved-user fixture for expiry/read-policy verification.
        await test.Repository.AppendUserMessageAsync(test.Actor,
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
        var conversation = await test.SeedAsync("Private Learning text");
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
        var conversation = await test.SeedAsync("Fictional content", operation);
        test.Authorization.Current = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), "Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.FindConversationAsync(test.Actor.Module,
                conversation.ConversationId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.ListConversationsAsync(test.Actor.Module, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            test.Service.ReadRecentMessagesAsync(test.Actor.Module,
                conversation.ConversationId, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Denied"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
        var conversation = await test.SeedAsync("Private", operation);
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
            test.Service.StartConversationAsync(test.Actor.Module, operation, "Private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(test.Actor.Module,
                conversation.ConversationId, Guid.NewGuid(), "Resurrect"));
    }

    [Fact]
    public async Task PreCancelledToken_StopsAllMethodsWithoutWrites()
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("First", operation);
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

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task ValidLegacyAssistantWrite_IsDeniedWithoutAnyDependencyCalls(AiModule module)
    {
        var test = new Fixture(module);
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("Synthetic user", operation);
        var messages = await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10);
        var calls = test.Recording.Calls;
        var authCalls = test.Authorization.Calls;
        const string privateText = "private assistant தமிழ் සිංහල [MOCK]";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.SaveAssistantReplyAsync(module, conversation.ConversationId, operation, privateText));

        Assert.Contains("disabled", error.Message);
        Assert.Null(error.InnerException);
        foreach (var value in new[] { privateText, test.Actor.UserId.ToString(), test.Actor.ModuleProfileId.ToString(), conversation.ConversationId.ToString(), operation.ToString() })
            Assert.DoesNotContain(value, error.ToString());
        Assert.Equal(calls, test.Recording.Calls);
        Assert.Equal(authCalls, test.Authorization.Calls);
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
        Assert.Equal(messages, await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
    }

    [Theory]
    [InlineData("missing-actor")]
    [InlineData("empty-account")]
    [InlineData("empty-profile")]
    [InlineData("foreign-account")]
    [InlineData("foreign-profile")]
    [InlineData("foreign-module")]
    [InlineData("unknown-module")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("missing-conversation")]
    public async Task InvalidOrInaccessibleContextCannotEnableLegacyWrite(string context)
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("Owner user", operation);
        if (context == "deleted")
            await test.Service.DeleteConversationAsync(test.Actor.Module, conversation.ConversationId);
        if (context == "expired") test.Clock.Advance(TimeSpan.FromDays(14));
        test.Authorization.Current = context switch
        {
            "missing-actor" => null,
            "empty-account" => test.Actor with { UserId = Guid.Empty },
            "empty-profile" => test.Actor with { ModuleProfileId = Guid.Empty },
            "foreign-account" => test.Actor with { UserId = Guid.NewGuid() },
            "foreign-profile" => test.Actor with { ModuleProfileId = Guid.NewGuid() },
            "foreign-module" => test.Actor with { Module = AiModule.Hobbies },
            _ => test.Actor
        };
        var calls = test.Recording.Calls;
        var authCalls = test.Authorization.Calls;
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.SaveAssistantReplyAsync(
                context == "unknown-module" ? (AiModule)99 : test.Actor.Module,
                context == "missing-conversation" ? Guid.NewGuid() : conversation.ConversationId,
                operation, "Valid assistant text"));
        Assert.Equal(calls, test.Recording.Calls);
        Assert.Equal(authCalls, test.Authorization.Calls);
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
        test.Authorization.Current = test.Actor;
        if (context is "deleted" or "expired")
        {
            Assert.Null(await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
            Assert.Empty(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        }
        else
        {
            Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
            Assert.Equal(AiStoredMessageRole.User, Assert.Single(await test.Repository.ReadRecentMessagesAsync(
                test.Actor, conversation.ConversationId, 10)).Role);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCannotWriteOrTriggerAutomaticRetry(bool preCancelled)
    {
        var test = new Fixture();
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("First", operation);
        using var cancellation = new CancellationTokenSource();
        if (preCancelled) cancellation.Cancel();
        var calls = test.Recording.Calls;
        if (preCancelled)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.SaveAssistantReplyAsync(
                test.Actor.Module, conversation.ConversationId, operation, "Reply", cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            // Disabled path has no async dependency work to await or cancel; later cancellation
            // cannot turn its already faulted Task into a successful write or a retry.
            var pending = test.Service.SaveAssistantReplyAsync(test.Actor.Module, conversation.ConversationId,
                operation, "Reply", cancellation.Token);
            Assert.True(pending.IsFaulted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }
        Assert.Equal(calls, test.Recording.Calls);
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
        Assert.Single(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
    }

    [Theory]
    [InlineData("find")]
    [InlineData("list")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task UnrelatedLifecycleRepositoryFailuresPropagateWithoutRetry(string action)
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
        var failure = new InvalidOperationException("Synthetic storage unavailable");
        test.Recording.Failure = failure;
        var calls = test.Recording.Calls;
        Task Invoke() => action switch
        {
            "find" => test.Service.FindConversationAsync(test.Actor.Module, conversation.ConversationId),
            "list" => test.Service.ListConversationsAsync(test.Actor.Module, 10),
            "read" => test.Service.ReadRecentMessagesAsync(test.Actor.Module, conversation.ConversationId, 10),
            _ => test.Service.DeleteConversationAsync(test.Actor.Module, conversation.ConversationId)
        };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(Invoke));
        Assert.Equal(calls + 1, test.Recording.Calls);
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
        Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
        Assert.Single(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
    }

    [Fact]
    public async Task DisabledPathDoesNotConsultFailingAuthorizationOrRepository()
    {
        var test = new Fixture();
        test.Recording.Failure = new InvalidOperationException("Must not reach storage");
        var authorization = new ThrowingAuthorization();
        var service = new AiConversationLifecycleService(authorization, test.Recording);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAssistantReplyAsync(
            AiModule.Learning, Guid.NewGuid(), Guid.NewGuid(), "Valid text"));
        Assert.Contains("disabled", error.Message);
        Assert.Equal(0, authorization.Calls);
        Assert.Equal(0, test.Recording.Calls);
        Assert.Equal(0, test.Recording.AssistantAppendCalls);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task ValidUserWritesFailWithoutAuthorizationStorageOrExpiryChanges(AiModule module)
    {
        var test = new Fixture(module);
        var operation = Guid.NewGuid();
        var conversation = await test.SeedAsync("Existing user", operation);
        test.Clock.Advance(TimeSpan.FromDays(13));
        var messages = await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10);
        var calls = test.Recording.Calls;
        var authCalls = test.Authorization.Calls;
        const string privateText = "private current தமிழ் සිංහල [MOCK]";
        var creation = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.StartConversationAsync(module, operation, privateText));
        var continuation = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.ContinueConversationAsync(module, conversation.ConversationId, operation, privateText));
        foreach (var error in new[] { creation, continuation })
        {
            Assert.Contains("disabled", error.Message);
            Assert.Contains("guarded admission", error.Message);
            Assert.Null(error.InnerException);
            foreach (var value in new[] { privateText, test.Actor.UserId.ToString(), test.Actor.ModuleProfileId.ToString(), operation.ToString() })
                Assert.DoesNotContain(value, error.ToString());
        }
        Assert.Equal(calls, test.Recording.Calls);
        Assert.Equal(authCalls, test.Authorization.Calls);
        Assert.Equal(0, test.Recording.CreateCalls);
        Assert.Equal(0, test.Recording.UserAppendCalls);
        Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
        Assert.Equal(messages, await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        Assert.Single(await test.Repository.ListAsync(test.Actor, 10));
        test.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await test.Service.FindConversationAsync(module, conversation.ConversationId));
    }

    [Theory]
    [InlineData("missing-actor")]
    [InlineData("empty-account")]
    [InlineData("empty-profile")]
    [InlineData("foreign-account")]
    [InlineData("foreign-profile")]
    [InlineData("foreign-module")]
    [InlineData("unknown-module")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("empty-conversation")]
    [InlineData("empty-operation")]
    [InlineData("missing-conversation")]
    public async Task ContextOrIdentifiersCannotEnableLegacyUserWrites(string context)
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("Owner user");
        if (context == "expired") test.Clock.Advance(TimeSpan.FromDays(14));
        if (context == "deleted") await test.Service.DeleteConversationAsync(test.Actor.Module, conversation.ConversationId);
        test.Authorization.Current = context switch
        {
            "missing-actor" => null,
            "empty-account" => test.Actor with { UserId = Guid.Empty },
            "empty-profile" => test.Actor with { ModuleProfileId = Guid.Empty },
            "foreign-account" => test.Actor with { UserId = Guid.NewGuid() },
            "foreign-profile" => test.Actor with { ModuleProfileId = Guid.NewGuid() },
            "foreign-module" => test.Actor with { Module = AiModule.Hobbies },
            _ => test.Actor
        };
        var module = context == "unknown-module" ? (AiModule)99 : test.Authorization.Current?.Module ?? test.Actor.Module;
        var operation = context == "empty-operation" ? Guid.Empty : Guid.NewGuid();
        var conversationId = context == "empty-conversation" ? Guid.Empty : context == "missing-conversation" ? Guid.NewGuid() : conversation.ConversationId;
        var calls = test.Recording.Calls;
        var authCalls = test.Authorization.Calls;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.StartConversationAsync(module, operation, "User"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service.ContinueConversationAsync(module, conversationId, operation, "User"));
        }
        Assert.Equal(calls, test.Recording.Calls);
        Assert.Equal(authCalls, test.Authorization.Calls);
        Assert.Equal(0, test.Recording.CreateCalls);
        Assert.Equal(0, test.Recording.UserAppendCalls);
        test.Authorization.Current = test.Actor;
        if (context is "expired" or "deleted")
            Assert.Null(await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
        else
        {
            Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
            Assert.Single(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledLegacyUserWritesCannotCreateAppendOrRetry(bool preCancelled)
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("First");
        using var cancellation = new CancellationTokenSource();
        if (preCancelled) cancellation.Cancel();
        Func<Task>[] actions =
        {
            () => test.Service.StartConversationAsync(test.Actor.Module, Guid.NewGuid(), "Cancelled", cancellation.Token),
            () => test.Service.ContinueConversationAsync(test.Actor.Module, conversation.ConversationId, Guid.NewGuid(), "Cancelled", cancellation.Token)
        };
        if (preCancelled)
        {
            foreach (var action in actions)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
        }
        else
        {
            var results = actions.Select(action => action()).ToArray();
            Assert.All(results, task => Assert.True(task.IsFaulted));
            cancellation.Cancel();
            foreach (var task in results) await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        Assert.Equal(0, test.Recording.Calls);
        Assert.Equal(0, test.Recording.CreateCalls);
        Assert.Equal(0, test.Recording.UserAppendCalls);
        Assert.Single(await test.Repository.ReadRecentMessagesAsync(test.Actor, conversation.ConversationId, 10));
        Assert.Equal(conversation, await test.Repository.FindAsync(test.Actor, conversation.ConversationId));
    }

    [Fact]
    public async Task DisabledUserWritesDoNotConsultFailingDependencies()
    {
        var test = new Fixture();
        test.Recording.Failure = new InvalidOperationException("Storage must not execute");
        var authorization = new ThrowingAuthorization();
        var service = new AiConversationLifecycleService(authorization, test.Recording);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartConversationAsync(AiModule.Learning, Guid.NewGuid(), "Valid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ContinueConversationAsync(AiModule.Learning, Guid.NewGuid(), Guid.NewGuid(), "Valid"));
        Assert.Equal(0, authorization.Calls);
        Assert.Equal(0, test.Recording.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    public async Task InvalidInitialActorStillBlocksEnabledReadsAndDeletion(string defect)
    {
        var test = new Fixture();
        var conversation = await test.SeedAsync("Owner");
        test.Authorization.Current = defect switch
        {
            "missing" => null,
            "account" => test.Actor with { UserId = Guid.Empty },
            "profile" => test.Actor with { ModuleProfileId = Guid.Empty },
            _ => test.Actor with { Module = AiModule.Hobbies }
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => test.Service.FindConversationAsync(test.Actor.Module, conversation.ConversationId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => test.Service.ListConversationsAsync(test.Actor.Module, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => test.Service.ReadRecentMessagesAsync(test.Actor.Module, conversation.ConversationId, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => test.Service.DeleteConversationAsync(test.Actor.Module, conversation.ConversationId));
        Assert.Equal(0, test.Recording.Calls);
        test.Authorization.Current = test.Actor;
        Assert.NotNull(await test.Service.FindConversationAsync(test.Actor.Module, conversation.ConversationId));
    }

    private sealed class ThrowingAuthorization : IAiActorAuthorization
    {
        public int Calls { get; private set; }
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Must not reach authorization");
        }
    }

    // Test-only decorator: observes ordinary service writes without changing the shared fake.
    private sealed class RecordingRepository(IAiConversationRepository inner) : IAiConversationRepository
    {
        public int Calls { get; private set; }
        public int AssistantAppendCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int UserAppendCalls { get; private set; }
        public Exception? Failure { get; set; }
        private void Record()
        {
            Calls++;
            if (Failure is not null) throw Failure;
        }
        public Task<AiStoredConversation> CreateWithUserMessageAsync(AiAuthorizedActor actor, Guid operationId, string validatedUserText, CancellationToken cancellationToken = default)
        { CreateCalls++; Record(); return inner.CreateWithUserMessageAsync(actor, operationId, validatedUserText, cancellationToken); }
        public Task<AiStoredConversation?> FindAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken cancellationToken = default)
        { Record(); return inner.FindAsync(actor, conversationId, cancellationToken); }
        public Task<IReadOnlyList<AiStoredConversation>> ListAsync(AiAuthorizedActor actor, int limit, CancellationToken cancellationToken = default)
        { Record(); return inner.ListAsync(actor, limit, cancellationToken); }
        public Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(AiAuthorizedActor actor, Guid conversationId, int limit, CancellationToken cancellationToken = default)
        { Record(); return inner.ReadRecentMessagesAsync(actor, conversationId, limit, cancellationToken); }
        public Task<AiStoredAiMessage> AppendUserMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string validatedUserText, CancellationToken cancellationToken = default)
        { UserAppendCalls++; Record(); return inner.AppendUserMessageAsync(actor, conversationId, operationId, validatedUserText, cancellationToken); }
        public Task<AiStoredAiMessage> AppendAssistantMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string assistantText, CancellationToken cancellationToken = default)
        { AssistantAppendCalls++; Record(); return inner.AppendAssistantMessageAsync(actor, conversationId, operationId, assistantText, cancellationToken); }
        public Task DeleteAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken cancellationToken = default)
        { Record(); return inner.DeleteAsync(actor, conversationId, cancellationToken); }
    }

    private sealed class Fixture
    {
        public AiAuthorizedActor Actor { get; }
        public MutableAuthorization Authorization { get; }
        public ManualClock Clock { get; }
        public InMemoryAiConversationRepository Repository { get; }
        public RecordingRepository Recording { get; }
        public AiConversationLifecycleService Service { get; }

        public Fixture(AiModule module = AiModule.Learning)
        {
            Actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
            Authorization = new MutableAuthorization(Actor);
            Clock = new ManualClock(StartUtc);
            Repository = new InMemoryAiConversationRepository(Authorization, Clock);
            Recording = new RecordingRepository(Repository);
            Service = new AiConversationLifecycleService(Authorization, Recording);
        }

        public Task<AiStoredConversation> SeedAsync(
            string? text, Guid? operationId = null) =>
            Repository.CreateWithUserMessageAsync(Actor,
                operationId ?? Guid.NewGuid(), text!);
    }

    private sealed class MutableAuthorization(AiAuthorizedActor? current)
        : IAiActorAuthorization
    {
        public AiAuthorizedActor? Current { get; set; } = current;
        public int Calls { get; private set; }

        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
            AiModule module, CancellationToken cancellationToken = default)
        {
            Calls++;
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
