
using System;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic snapshot tests only. No database, provider, or real authorization.
/// </summary>
public sealed class AiLateReplyPolicyTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void ValidReply_IsAcceptedWithoutRenewingExpiry(AiModule module)
    {
        var s = Create(module);
        var last = s.Conversation.LastSavedUserMessageAtUtc;
        var expiry = s.Conversation.ExpiresAtUtc;
        var user = s.User.ToStoredMessage();
        var reply = s.Reply.ToStoredMessage();

        Assert.True(s.Evaluate());
        Assert.Equal(last, s.Conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(expiry, s.Conversation.ExpiresAtUtc);
        Assert.Null(s.Conversation.DeletedAtUtc);
        Assert.Equal(user, s.User.ToStoredMessage());
        Assert.Equal(reply, s.Reply.ToStoredMessage());
    }

    [Fact]
    public void LatestNewUserOperation_IsAccepted()
    {
        var s = Create();
        var next = SaveNext(s, Created.AddMinutes(2));

        Assert.True(next.Evaluate());
        Assert.Equal(next.User.SavedAtUtc.AddDays(14),
            next.Conversation.ExpiresAtUtc);
    }

    [Fact]
    public void OneTickBeforeExpiry_IsAccepted()
    {
        var s = Create();
        var now = s.Conversation.ExpiresAtUtc.AddTicks(-1);
        var reply = AiMessageEntity.CreateAssistantMessage(
            s.Conversation, s.Dispatched!, s.OperationId, "Reply", now);

        Assert.True((s with { Now = now, Reply = reply }).Evaluate());
    }

    [Fact]
    public void OffsetClock_IsNormalized()
    {
        var s = Create();
        Assert.True((s with
        {
            Now = s.Now.ToOffset(TimeSpan.FromMinutes(330))
        }).Evaluate());
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("dispatch-null")]
    [InlineData("current-null")]
    [InlineData("conversation-id-empty")]
    [InlineData("operation-id-empty")]
    [InlineData("conversation-null")]
    [InlineData("user-null")]
    [InlineData("reply-null")]
    [InlineData("clock-default")]
    public void MissingEvidence_IsDenied(string defect)
    {
        var s = Create();
        var bad = defect switch
        {
            "unverified" => s with { Verified = false },
            "dispatch-null" => s with { Dispatched = null },
            "current-null" => s with { Current = null },
            "conversation-id-empty" => s with { ConversationId = Guid.Empty },
            "operation-id-empty" => s with { OperationId = Guid.Empty },
            "conversation-null" => s with { Conversation = null! },
            "user-null" => s with { User = null! },
            "reply-null" => s with { Reply = null! },
            "clock-default" => s with { Now = default },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        Assert.False(bad.Evaluate());
    }

    [Theory]
    [InlineData("changed-account")]
    [InlineData("changed-profile")]
    [InlineData("changed-module")]
    [InlineData("both-other-account")]
    [InlineData("both-other-profile")]
    [InlineData("both-other-module")]
    [InlineData("invalid-account")]
    [InlineData("invalid-profile")]
    [InlineData("invalid-module")]
    public void WrongOrInvalidIdentity_IsDenied(string defect)
    {
        var s = Create();
        var actor = s.Dispatched!;
        var wrong = defect switch
        {
            "changed-account" or "both-other-account" =>
                actor with { UserId = Guid.NewGuid() },
            "changed-profile" or "both-other-profile" =>
                actor with { ModuleProfileId = Guid.NewGuid() },
            "changed-module" or "both-other-module" =>
                actor with { Module = AiModule.Hobbies },
            "invalid-account" => actor with { UserId = Guid.Empty },
            "invalid-profile" => actor with { ModuleProfileId = Guid.Empty },
            "invalid-module" => actor with { Module = (AiModule)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var bad = defect.StartsWith("changed-", StringComparison.Ordinal)
            ? s with { Current = wrong }
            : s with { Dispatched = wrong, Current = wrong };

        Assert.False(bad.Evaluate());
    }

    [Fact]
    public void DifferentConversationId_IsDenied()
    {
        var s = Create();
        Assert.False((s with { ConversationId = Guid.NewGuid() }).Evaluate());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("at-expiry")]
    [InlineData("after-expiry")]
    [InlineData("before-user")]
    public void RevokedOrInvalidLifecycle_IsDenied(string state)
    {
        var s = Create();
        var now = state switch
        {
            "deleted" => s.Now.AddSeconds(1),
            "at-expiry" => s.Conversation.ExpiresAtUtc,
            "after-expiry" => s.Conversation.ExpiresAtUtc.AddTicks(1),
            "before-user" => Created.AddTicks(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

        if (state == "deleted")
            s.Conversation.MarkDeleted(now);

        var expiry = s.Conversation.ExpiresAtUtc;
        var deletion = s.Conversation.DeletedAtUtc;
        Assert.False((s with { Now = now }).Evaluate());
        Assert.Equal(expiry, s.Conversation.ExpiresAtUtc);
        Assert.Equal(deletion, s.Conversation.DeletedAtUtc);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("user")]
    [InlineData("reply")]
    public void MismatchedOperation_IsDenied(string field)
    {
        var s = Create();
        var wrong = Guid.NewGuid();

        if (field == "request")
            s = s with { OperationId = wrong };
        else if (field == "user")
            Set(s.User, nameof(AiMessageEntity.OperationId), wrong);
        else if (field == "reply")
            Set(s.Reply, nameof(AiMessageEntity.OperationId), wrong);
        else
            throw new ArgumentOutOfRangeException(nameof(field));

        Assert.False(s.Evaluate());
    }

    [Fact]
    public void OlderOperationAfterNewUserMessage_IsDenied()
    {
        var original = Create();
        var latest = SaveNext(original, Created.AddMinutes(2));

        Assert.False((original with { Now = latest.Now }).Evaluate());
        Assert.True(latest.Evaluate());
    }

    [Fact]
    public void SameTimestampSupersession_RequiresOperationVerification()
    {
        var original = Create();
        var latest = SaveNext(original, Created);

        Assert.Equal(original.User.SavedAtUtc, latest.User.SavedAtUtc);
        Assert.NotEqual(original.OperationId, latest.OperationId);

        // The trusted operation ledger must mark the stale operation false.
        Assert.False((original with { Verified = false }).Evaluate());
        Assert.True(latest.Evaluate());
    }

    [Theory]
    [InlineData("bad-expiry")]
    [InlineData("empty-create-operation")]
    [InlineData("user-role")]
    [InlineData("reply-role")]
    [InlineData("user-account")]
    [InlineData("reply-profile")]
    [InlineData("user-module")]
    [InlineData("reply-conversation")]
    [InlineData("duplicate-message-id")]
    [InlineData("empty-user-id")]
    [InlineData("blank-reply")]
    [InlineData("future-reply")]
    [InlineData("reply-before-user")]
    [InlineData("reply-offset")]
    public void MalformedPersistedSnapshot_IsDenied(string defect)
    {
        var s = Create();

        switch (defect)
        {
            case "bad-expiry":
                Set(s.Conversation, nameof(AiConversationEntity.ExpiresAtUtc),
                    s.Conversation.ExpiresAtUtc.AddHours(1));
                break;
            case "empty-create-operation":
                Set(s.Conversation, nameof(AiConversationEntity.CreateOperationId),
                    Guid.Empty);
                break;
            case "user-role":
                Set(s.User, nameof(AiMessageEntity.Role), AiStoredMessageRole.Assistant);
                break;
            case "reply-role":
                Set(s.Reply, nameof(AiMessageEntity.Role), AiStoredMessageRole.User);
                break;
            case "user-account":
                Set(s.User, nameof(AiMessageEntity.AccountId), Guid.NewGuid());
                break;
            case "reply-profile":
                Set(s.Reply, nameof(AiMessageEntity.ModuleProfileId), Guid.NewGuid());
                break;
            case "user-module":
                Set(s.User, nameof(AiMessageEntity.Module), AiModule.Social);
                break;
            case "reply-conversation":
                Set(s.Reply, nameof(AiMessageEntity.ConversationId), Guid.NewGuid());
                break;
            case "duplicate-message-id":
                Set(s.Reply, nameof(AiMessageEntity.Id), s.User.Id);
                break;
            case "empty-user-id":
                Set(s.User, nameof(AiMessageEntity.Id), Guid.Empty);
                break;
            case "blank-reply":
                Set(s.Reply, nameof(AiMessageEntity.Text), " ");
                break;
            case "future-reply":
                Set(s.Reply, nameof(AiMessageEntity.SavedAtUtc), s.Now.AddTicks(1));
                break;
            case "reply-before-user":
                s = SaveNext(s, Created.AddMinutes(2));
                Set(s.Reply, nameof(AiMessageEntity.SavedAtUtc),
                    s.User.SavedAtUtc.AddTicks(-1));
                break;
            case "reply-offset":
                Set(s.Reply, nameof(AiMessageEntity.SavedAtUtc),
                    s.Reply.SavedAtUtc.ToOffset(TimeSpan.FromHours(1)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.False(s.Evaluate());
    }

    // Helpers: simulate persisted snapshots, never real database writes.

    private static Snapshot Create(AiModule module = AiModule.Learning)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
        var operation = Guid.NewGuid();
        var conversation = AiConversationEntity.CreateForFirstUserMessage(
            actor, operation, Created);
        var user = AiMessageEntity.CreateUserMessage(
            conversation, actor, operation, "Synthetic question", Created);
        var now = Created.AddSeconds(1);
        var reply = AiMessageEntity.CreateAssistantMessage(
            conversation, actor, operation, "Synthetic reply", now);

        return new(actor, actor with { }, conversation.Id, operation,
            conversation, user, reply, now, true);
    }

    private static Snapshot SaveNext(Snapshot s, DateTimeOffset savedAt)
    {
        var actor = s.Dispatched!;
        var operation = Guid.NewGuid();
        var user = AiMessageEntity.CreateUserMessage(
            s.Conversation, actor, operation, "Next question", savedAt);

        AiMessagePersistencePolicy.ValidateNewUserMessage(s.Conversation, user);
        s.Conversation.ApplySavedUserMessage(savedAt);

        var now = savedAt.AddSeconds(1);
        var reply = AiMessageEntity.CreateAssistantMessage(
            s.Conversation, actor, operation, "Next reply", now);

        return s with { OperationId = operation, User = user, Reply = reply, Now = now };
    }

    // Test-only simulation of corrupt ORM-materialized metadata.
    private static void Set(object row, string property, object value) =>
        row.GetType().GetProperty(property)!.GetSetMethod(true)!
            .Invoke(row, new[] { value });

    private sealed record Snapshot(
        AiAuthorizedActor? Dispatched,
        AiAuthorizedActor? Current,
        Guid ConversationId,
        Guid OperationId,
        AiConversationEntity Conversation,
        AiMessageEntity User,
        AiMessageEntity Reply,
        DateTimeOffset Now,
        bool Verified)
    {
        public bool Evaluate() => AiLateReplyPolicy.CanAccept(
            Dispatched, Current, ConversationId, OperationId,
            Conversation, User, Reply, Now, Verified);
    }
}
