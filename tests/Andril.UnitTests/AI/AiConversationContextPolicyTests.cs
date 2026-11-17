using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Pure context selection tests; no provider, live identity or database.</summary>
public sealed class AiConversationContextPolicyTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void FreshChat_ContainsOnlyItsCurrentMessage(AiModule module)
    {
        var s = Create(module, fresh: true);
        var result = Select(s);
        Assert.Equal(s.Messages[0].ToStoredMessage(), Assert.Single(result));
    }

    [Fact]
    public void SufficientBudget_PreservesChronologicalOrderAndRoles()
    {
        var s = Create();
        Assert.Equal(s.Messages.Select(m => m.ToStoredMessage()).ToArray(),
            Select(s).ToArray());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    [InlineData(100, 5)]
    public void MessageLimit_SelectsRecentWindowStartingWithUser(int limit, int expected)
    {
        var s = Create();
        var result = Select(s, limit: limit);
        Assert.Equal(s.Messages.TakeLast(expected).Select(m => m.ToStoredMessage()).ToArray(),
            result.ToArray());
        Assert.Equal(AiStoredMessageRole.User, result[0].Role);
        Assert.Equal(s.Current.Id, result[^1].MessageId);
    }

    [Fact]
    public void FullInputBudget_IncludesOverheadAndDropsOlderMessages()
    {
        var s = Create();
        // Synthetic count: 1700 tokens of overhead + 100 per message.
        var result = Select(s, count: messages => 1700 + 100 * messages.Count);
        Assert.Equal(s.Messages.TakeLast(3).Select(m => m.ToStoredMessage()).ToArray(),
            result.ToArray());
    }

    [Fact]
    public void Budget_AllowsExactly2000TokensForCurrentMessage()
    {
        var s = Create();
        Assert.Equal(s.Current.ToStoredMessage(),
            Assert.Single(Select(s, count: m => 2000 + (m.Count - 1) * 100)));
    }

    [Theory]
    [InlineData(2001, "AI input exceeds the permitted budget.")]
    [InlineData(0, "AI input token count unavailable.")]
    [InlineData(-1, "AI input token count unavailable.")]
    public void Budget_InvalidOrExcessiveCurrentInputIsRejected(int tokens, string error)
    {
        Assert.Equal(error, Assert.Throws<InvalidOperationException>(() =>
            Select(Create(), count: _ => tokens)).Message);
    }

    [Fact]
    public void CounterFailure_PropagatesWithoutReturningContext()
    {
        Assert.Throws<InvalidOperationException>(() => Select(Create(),
            count: _ => throw new InvalidOperationException("Counter unavailable")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidMessageLimit_IsRejected(int limit)
    {
        Assert.Equal("maxMessages", Assert.Throws<ArgumentOutOfRangeException>(() =>
            Select(Create(), limit: limit)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidOrMismatchedActor_DeniedBeforeTokenCounting(int defect)
    {
        var s = Create();
        AiAuthorizedActor? actor = defect switch
        {
            0 => null,
            1 => s.Actor with { UserId = Guid.Empty },
            2 => s.Actor with { UserId = Guid.NewGuid() },
            3 => s.Actor with { ModuleProfileId = Guid.NewGuid() },
            4 => s.Actor with { Module = AiModule.Hobbies },
            _ => s.Actor with { Module = (AiModule)99 }
        };
        Denied(s, actor, s.Messages, s.Current.Id, s.Now);
    }

    [Fact]
    public void ExpiredAtExactDeadline_IsDenied()
    {
        var s = Create();
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Conversation.ExpiresAtUtc);
    }

    [Fact]
    public void DeletedConversation_IsDenied()
    {
        var s = Create();
        s.Conversation.MarkDeleted(s.Now);
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    [Fact]
    public void BeforeExpiry_WithNonUtcClock_IsAllowedWithoutRenewal()
    {
        var s = Create();
        var expiry = s.Conversation.ExpiresAtUtc;
        var last = s.Conversation.LastSavedUserMessageAtUtc;
        var local = expiry.AddTicks(-1).ToOffset(TimeSpan.FromMinutes(330));
        Assert.NotEmpty(Select(s, now: local));
        Assert.Equal(expiry, s.Conversation.ExpiresAtUtc);
        Assert.Equal(last, s.Conversation.LastSavedUserMessageAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingWrongOrStaleCurrentMessage_IsDenied(int defect)
    {
        var s = Create();
        var id = defect switch
        {
            0 => Guid.Empty,
            1 => Guid.NewGuid(),
            _ => s.Messages[0].Id
        };
        Denied(s, s.Actor, s.Messages, id, s.Now);
    }

    [Fact]
    public void AssistantAsCurrentMessage_IsDenied()
    {
        var s = Create();
        var reply = AiMessageEntity.CreateAssistantMessage(s.Conversation, s.Actor,
            s.Current.OperationId, "Reply", s.Now);
        var messages = s.Messages.Append(reply).ToArray();
        Denied(s, s.Actor, messages, reply.Id, s.Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void EmptyOrOversizedCandidateList_IsDenied(int count)
    {
        var s = Create();
        Denied(s, s.Actor, Enumerable.Repeat(s.Current, count).ToArray(), s.Current.Id, s.Now);
    }

    [Fact]
    public void ForeignConversation_IsDeniedEvenWhenOlderMessageWouldBeExcluded()
    {
        var s = Create();
        var other = AiConversationEntity.CreateForFirstUserMessage(s.Actor, Guid.NewGuid(), Start);
        s.Messages[0] = AiMessageEntity.CreateUserMessage(other, s.Actor,
            other.CreateOperationId, "Foreign context", Start);
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    [Theory]
    [InlineData("AccountId")]
    [InlineData("ModuleProfileId")]
    [InlineData("Module")]
    [InlineData("Id")]
    [InlineData("OperationId")]
    [InlineData("Role")]
    [InlineData("Text")]
    [InlineData("SavedAtUtc")]
    public void MalformedOlderRecord_IsDeniedBeforeCounting(string property)
    {
        var s = Create();
        object value = property switch
        {
            "Module" => AiModule.Hobbies,
            "Role" => (AiStoredMessageRole)99,
            "Text" => " ",
            "SavedAtUtc" => Start.ToOffset(TimeSpan.FromMinutes(330)),
            _ => Guid.Empty
        };
        Corrupt(s.Messages[0], property, value);
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("OperationId")]
    public void DuplicateMessageIdOrSameRoleOperation_IsDenied(string property)
    {
        var s = Create();
        var source = typeof(AiMessageEntity).GetProperty(property)!;
        Corrupt(s.Messages[2], property, source.GetValue(s.Messages[0])!);
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    [Fact]
    public void OutOfOrderRecords_AreDenied()
    {
        var s = Create();
        (s.Messages[0], s.Messages[1]) = (s.Messages[1], s.Messages[0]);
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    [Fact]
    public void FutureCurrentMessage_IsDenied()
    {
        var s = Create();
        Denied(s, s.Actor, s.Messages, s.Current.Id, s.Current.SavedAtUtc.AddTicks(-1));
    }

    [Fact]
    public void DefaultServerClock_IsDenied()
    {
        var s = Create();
        Denied(s, s.Actor, s.Messages, s.Current.Id, default);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void PersistedUserText_RespectsUnicodeTextElementLimit(int length, bool allowed)
    {
        var s = Create();
        var text = string.Concat(Enumerable.Repeat("😀", length));
        // Simulates storage materialization, bypassing the factory for invalid rows.
        Corrupt(s.Current, nameof(AiMessageEntity.Text), text);
        if (allowed)
            Assert.Equal(text, Select(s)[^1].Text);
        else
            Denied(s, s.Actor, s.Messages, s.Current.Id, s.Now);
    }

    private static Scenario Create(AiModule module = AiModule.Learning, bool fresh = false)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
        var c = AiConversationEntity.CreateForFirstUserMessage(actor, Guid.NewGuid(), Start);
        var first = AiMessageEntity.CreateUserMessage(c, actor, c.CreateOperationId, "First", Start);
        if (fresh) return new(actor, c, new[] { first }, Start);
        var reply = AiMessageEntity.CreateAssistantMessage(c, actor, first.OperationId, "Reply 1", Start.AddMinutes(1));
        var second = AiMessageEntity.CreateUserMessage(c, actor, Guid.NewGuid(), "Second", Start.AddMinutes(2));
        c.ApplySavedUserMessage(second.SavedAtUtc);
        var reply2 = AiMessageEntity.CreateAssistantMessage(c, actor, second.OperationId, "Reply 2", Start.AddMinutes(3));
        var current = AiMessageEntity.CreateUserMessage(c, actor, Guid.NewGuid(), "Current", Start.AddMinutes(4));
        c.ApplySavedUserMessage(current.SavedAtUtc);
        return new(actor, c, new[] { first, reply, second, reply2, current }, Start.AddMinutes(5));
    }

    private static IReadOnlyList<AiStoredAiMessage> Select(Scenario s, int limit = 100,
        Func<IReadOnlyList<AiStoredAiMessage>, int>? count = null, DateTimeOffset? now = null) =>
        AiConversationContextPolicy.SelectRecentMessages(s.Actor, s.Conversation,
            s.Messages, s.Current.Id, now ?? s.Now, limit, count ?? (m => 100 * m.Count));

    private static void Denied(Scenario s, AiAuthorizedActor? actor,
        IReadOnlyList<AiMessageEntity> rows, Guid currentId, DateTimeOffset now)
    {
        var calls = 0;
        var error = Assert.Throws<InvalidOperationException>(() =>
            AiConversationContextPolicy.SelectRecentMessages(actor, s.Conversation,
                rows, currentId, now, 1, _ => { calls++; return 100; }));
        Assert.Equal("AI conversation context unavailable.", error.Message);
        Assert.Equal(0, calls);
    }

    private static void Corrupt(AiMessageEntity message, string property, object value) =>
        typeof(AiMessageEntity).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!
            .GetSetMethod(nonPublic: true)!.Invoke(message, new[] { value });

    private sealed record Scenario(AiAuthorizedActor Actor, AiConversationEntity Conversation,
        AiMessageEntity[] Messages, DateTimeOffset Now)
    {
        public AiMessageEntity Current => Messages[^1];
    }
}
