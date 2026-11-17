
using System;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 13: pure, synthetic-data consistency tests.
/// They do NOT prove database authorization, transactional persistence,
/// server-side operation lookup, unique indexes, or production deletion.
/// </summary>
public sealed class AiMessagePersistencePolicyTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc =
        CreatedAtUtc.AddDays(14);

    // FIRST USER MESSAGE -----------------------------------------------

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void FirstUserMessage_ValidForEachModule(AiModule module)
    {
        var actor = Actor(module);
        var conversation = Conversation(actor);
        var first = User(conversation, actor, conversation.CreateOperationId);

        AiMessagePersistencePolicy.ValidateFirstUserMessage(conversation, first);

        Assert.Equal(module, first.Module);
        Assert.Equal(conversation.Id, first.ConversationId);
        Assert.Equal(conversation.CreateOperationId, first.OperationId);
    }

    [Fact]
    public void FirstUserMessage_ValidationDoesNotModifyConversation()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var first = User(conversation, actor, conversation.CreateOperationId);

        AiMessagePersistencePolicy.ValidateFirstUserMessage(conversation, first);

        Assert.Equal(CreatedAtUtc, conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(ExpiresAtUtc, conversation.ExpiresAtUtc);
        Assert.Null(conversation.DeletedAtUtc);
    }

    [Fact]
    public void FirstUserMessage_DifferentOperationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var incorrect = User(conversation, actor, Guid.NewGuid());

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, incorrect));
    }

    [Fact]
    public void FirstUserMessage_LaterTimestampIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var incorrect = User(
            conversation, actor, conversation.CreateOperationId,
            CreatedAtUtc.AddTicks(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, incorrect));
    }

    [Fact]
    public void FirstUserMessage_StaleAfterConversationRenewalIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var first = User(conversation, actor, conversation.CreateOperationId);
        conversation.ApplySavedUserMessage(CreatedAtUtc.AddMinutes(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, first));
    }

    [Fact]
    public void FirstUserMessage_DeletedConversationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var first = User(conversation, actor, conversation.CreateOperationId);
        conversation.MarkDeleted(CreatedAtUtc.AddMinutes(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, first));
    }

    [Fact]
    public void FirstUserMessage_AssistantRoleIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var assistant = Assistant(
            conversation, actor, conversation.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, assistant));
    }

    [Fact]
    public void FirstUserMessage_OtherConversationSameOwnerIsRejected()
    {
        var actor = Actor();
        var expected = Conversation(actor);
        var another = Conversation(actor);
        var message = User(another, actor, another.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                expected, message));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FirstUserMessage_OtherActorOrModuleIsRejected(int mismatch)
    {
        var owner = Actor(AiModule.Learning);
        var conversation = Conversation(owner);
        var other = DifferentActor(owner, mismatch);
        var otherConversation = Conversation(other);
        var foreign = User(
            otherConversation, other, otherConversation.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, foreign));
    }

    [Fact]
    public void FirstUserMessage_NullConversationThrows()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var first = User(conversation, actor, conversation.CreateOperationId);

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(null!, first));

        Assert.Equal("conversation", error.ParamName);
    }

    [Fact]
    public void FirstUserMessage_NullMessageThrows()
    {
        var conversation = Conversation(Actor());

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateFirstUserMessage(
                conversation, null!));

        Assert.Equal("message", error.ParamName);
    }

    // NEW USER MESSAGE --------------------------------------------------

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void NewUserMessage_ValidForEachModule(AiModule module)
    {
        var actor = Actor(module);
        var conversation = Conversation(actor);
        var message = User(
            conversation, actor, Guid.NewGuid(), CreatedAtUtc.AddHours(1));

        AiMessagePersistencePolicy.ValidateNewUserMessage(
            conversation, message);

        Assert.Equal(module, message.Module);
        Assert.Equal(CreatedAtUtc, conversation.LastSavedUserMessageAtUtc);
    }

    [Fact]
    public void NewUserMessage_SameTimestampWithNewOperationIsPermitted()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(conversation, actor, Guid.NewGuid(), CreatedAtUtc);

        AiMessagePersistencePolicy.ValidateNewUserMessage(
            conversation, message);

        // Timestamp equality does not establish idempotency.
        // Database uniqueness and retry handling belong to the repository.
        Assert.Equal(CreatedAtUtc, conversation.LastSavedUserMessageAtUtc);
    }

    [Fact]
    public void NewUserMessage_OneTickBeforeExpiryIsPermitted()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(
            conversation, actor, Guid.NewGuid(), ExpiresAtUtc.AddTicks(-1));

        AiMessagePersistencePolicy.ValidateNewUserMessage(
            conversation, message);
    }

    [Fact]
    public void NewUserMessage_ReusingCreateOperationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(
            conversation, actor, conversation.CreateOperationId,
            CreatedAtUtc.AddHours(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, message));
    }

    [Fact]
    public void NewUserMessage_OlderThanLastSavedUserMessageIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        conversation.ApplySavedUserMessage(CreatedAtUtc.AddHours(2));
        var stale = User(
            conversation, actor, Guid.NewGuid(), CreatedAtUtc.AddHours(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, stale));
    }

    [Fact]
    public void NewUserMessage_AssistantRoleIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var assistant = Assistant(
            conversation, actor, Guid.NewGuid(), CreatedAtUtc.AddHours(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, assistant));
    }

    [Fact]
    public void NewUserMessage_DeletedConversationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(
            conversation, actor, Guid.NewGuid(), CreatedAtUtc.AddHours(1));
        conversation.MarkDeleted(CreatedAtUtc.AddMinutes(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, message));
    }

    [Fact]
    public void NewUserMessage_OtherConversationSameOwnerIsRejected()
    {
        var actor = Actor();
        var expected = Conversation(actor);
        var other = Conversation(actor);
        var message = User(other, actor, Guid.NewGuid());

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                expected, message));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NewUserMessage_OtherActorOrModuleIsRejected(int mismatch)
    {
        var owner = Actor(AiModule.Learning);
        var conversation = Conversation(owner);
        var other = DifferentActor(owner, mismatch);
        var otherConversation = Conversation(other);
        var foreign = User(otherConversation, other, Guid.NewGuid());

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, foreign));
    }

    [Fact]
    public void NewUserMessage_NullConversationThrows()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(conversation, actor, Guid.NewGuid());

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(null!, message));

        Assert.Equal("conversation", error.ParamName);
    }

    [Fact]
    public void NewUserMessage_NullMessageThrows()
    {
        var conversation = Conversation(Actor());

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateNewUserMessage(
                conversation, null!));

        Assert.Equal("message", error.ParamName);
    }

    [Fact]
    public void NewUserMessage_ValidationDoesNotRenewExpiry()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = User(
            conversation, actor, Guid.NewGuid(), CreatedAtUtc.AddDays(1));

        AiMessagePersistencePolicy.ValidateNewUserMessage(
            conversation, message);

        Assert.Equal(CreatedAtUtc, conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(ExpiresAtUtc, conversation.ExpiresAtUtc);
    }

    // ASSISTANT REPLY ---------------------------------------------------

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void AssistantReply_ToFirstUserOperationIsValid(AiModule module)
    {
        var actor = Actor(module);
        var conversation = Conversation(actor);
        var operationId = conversation.CreateOperationId;
        var accepted = User(conversation, actor, operationId);
        var reply = Assistant(
            conversation, actor, operationId, CreatedAtUtc.AddMinutes(1));

        AiMessagePersistencePolicy.ValidateAssistantReply(
            conversation, accepted, reply);

        Assert.Equal(module, reply.Module);
    }

    [Fact]
    public void AssistantReply_ToLaterSavedUserOperationIsValid()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();
        var userSavedAt = CreatedAtUtc.AddHours(1);
        var accepted = User(conversation, actor, operationId, userSavedAt);

        AiMessagePersistencePolicy.ValidateNewUserMessage(
            conversation, accepted);

        // Simulates the successful same-transaction user-message save.
        conversation.ApplySavedUserMessage(userSavedAt);

        var reply = Assistant(
            conversation, actor, operationId, userSavedAt.AddMinutes(1));

        AiMessagePersistencePolicy.ValidateAssistantReply(
            conversation, accepted, reply);
    }

    [Fact]
    public void AssistantReply_SameTimestampAsAcceptedUserIsValid()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);
        var reply = Assistant(
            conversation, actor, conversation.CreateOperationId);

        AiMessagePersistencePolicy.ValidateAssistantReply(
            conversation, accepted, reply);
    }

    [Fact]
    public void AssistantReply_DifferentOperationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);
        var reply = Assistant(conversation, actor, Guid.NewGuid());

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, reply));
    }

    [Fact]
    public void AssistantReply_EarlierThanAcceptedUserIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();
        var userSavedAt = CreatedAtUtc.AddHours(2);
        var accepted = User(conversation, actor, operationId, userSavedAt);
        conversation.ApplySavedUserMessage(userSavedAt);
        var reply = Assistant(
            conversation, actor, operationId, userSavedAt.AddTicks(-1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, reply));
    }

    [Fact]
    public void AssistantReply_UserOperationNotReflectedInParentIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();
        var accepted = User(
            conversation, actor, operationId, CreatedAtUtc.AddHours(1));
        var reply = Assistant(
            conversation, actor, operationId, CreatedAtUtc.AddHours(2));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, reply));
    }

    [Fact]
    public void AssistantReply_AcceptedMessageMustHaveUserRole()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = conversation.CreateOperationId;
        var incorrectAccepted = Assistant(conversation, actor, operationId);
        var reply = Assistant(
            conversation, actor, operationId, CreatedAtUtc.AddSeconds(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, incorrectAccepted, reply));
    }

    [Fact]
    public void AssistantReply_ReplyMustHaveAssistantRole()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = conversation.CreateOperationId;
        var accepted = User(conversation, actor, operationId);
        var incorrectReply = User(conversation, actor, operationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, incorrectReply));
    }

    [Fact]
    public void AssistantReply_DeletedConversationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = conversation.CreateOperationId;
        var accepted = User(conversation, actor, operationId);
        var reply = Assistant(
            conversation, actor, operationId, CreatedAtUtc.AddMinutes(1));
        conversation.MarkDeleted(CreatedAtUtc.AddSeconds(1));

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, reply));
    }

    [Fact]
    public void AssistantReply_AcceptedUserFromOtherConversationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var otherConversation = Conversation(actor);
        var accepted = User(
            otherConversation, actor, conversation.CreateOperationId);
        var reply = Assistant(
            conversation, actor, conversation.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, reply));
    }

    [Fact]
    public void AssistantReply_ReplyFromOtherConversationIsRejected()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var otherConversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);
        var foreignReply = Assistant(
            otherConversation, actor, conversation.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, foreignReply));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AssistantReply_OtherAccountProfileOrModuleIsRejected(int mismatch)
    {
        var owner = Actor(AiModule.Learning);
        var conversation = Conversation(owner);
        var accepted = User(
            conversation, owner, conversation.CreateOperationId);
        var outsider = DifferentActor(owner, mismatch);
        var foreignConversation = Conversation(outsider);
        var foreignReply = Assistant(
            foreignConversation, outsider, conversation.CreateOperationId);

        AssertInconsistent(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, foreignReply));
    }

    [Fact]
    public void AssistantReply_NullConversationThrows()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);
        var reply = Assistant(
            conversation, actor, conversation.CreateOperationId);

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                null!, accepted, reply));

        Assert.Equal("conversation", error.ParamName);
    }

    [Fact]
    public void AssistantReply_NullAcceptedUserThrows()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var reply = Assistant(
            conversation, actor, conversation.CreateOperationId);

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, null!, reply));

        Assert.Equal("message", error.ParamName);
    }

    [Fact]
    public void AssistantReply_NullReplyThrows()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);

        var error = Assert.Throws<ArgumentNullException>(() =>
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, accepted, null!));

        Assert.Equal("message", error.ParamName);
    }

    [Fact]
    public void AssistantReply_ValidationDoesNotRenewExpiry()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var accepted = User(
            conversation, actor, conversation.CreateOperationId);
        var reply = Assistant(
            conversation, actor, conversation.CreateOperationId,
            CreatedAtUtc.AddDays(2));

        AiMessagePersistencePolicy.ValidateAssistantReply(
            conversation, accepted, reply);

        Assert.Equal(CreatedAtUtc, conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(ExpiresAtUtc, conversation.ExpiresAtUtc);
    }

    // SYNTHETIC TEST HELPERS -------------------------------------------

    private static AiAuthorizedActor Actor(
        AiModule module = AiModule.Learning) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static AiConversationEntity Conversation(AiAuthorizedActor actor) =>
        AiConversationEntity.CreateForFirstUserMessage(
            actor, Guid.NewGuid(), CreatedAtUtc);

    private static AiMessageEntity User(
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid? operationId = null,
        DateTimeOffset? savedAt = null) =>
        AiMessageEntity.CreateUserMessage(
            conversation, actor, operationId ?? Guid.NewGuid(),
            "Synthetic user message", savedAt ?? CreatedAtUtc);

    private static AiMessageEntity Assistant(
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid? operationId = null,
        DateTimeOffset? savedAt = null) =>
        AiMessageEntity.CreateAssistantMessage(
            conversation, actor, operationId ?? Guid.NewGuid(),
            "Synthetic assistant reply", savedAt ?? CreatedAtUtc);

    private static AiAuthorizedActor DifferentActor(
        AiAuthorizedActor original, int mismatch) => mismatch switch
        {
            0 => original with { UserId = Guid.NewGuid() },
            1 => original with { ModuleProfileId = Guid.NewGuid() },
            _ => original with { Module = AiModule.Hobbies }
        };

    private static void AssertInconsistent(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "AI message persistence consistency check failed.",
            error.Message);
    }
}
