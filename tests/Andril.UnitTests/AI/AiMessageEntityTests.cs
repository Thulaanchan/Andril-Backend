
using System;
using System.Linq;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 11: synthetic, fixed-clock tests for message entity factories.
/// These tests do not verify live actor authorization, database uniqueness,
/// transactions, operation-to-user-message linking, or physical deletion.
/// </summary>
public sealed class AiMessageEntityTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc =
        CreatedAtUtc.AddDays(14);

    [Theory]
    [InlineData(AiModule.Learning, false)]
    [InlineData(AiModule.Hobbies, false)]
    [InlineData(AiModule.Social, false)]
    [InlineData(AiModule.Learning, true)]
    [InlineData(AiModule.Hobbies, true)]
    [InlineData(AiModule.Social, true)]
    public void Create_PreservesExactOwnershipModuleRoleAndOperation(
        AiModule module, bool assistant)
    {
        var actor = Actor(module);
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();

        var message = Make(
            assistant, conversation, actor, operationId,
            text: "A private test message");

        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.Equal(conversation.Id, message.ConversationId);
        Assert.Equal(actor.UserId, message.AccountId);
        Assert.Equal(actor.ModuleProfileId, message.ModuleProfileId);
        Assert.Equal(module, message.Module);
        Assert.Equal(operationId, message.OperationId);
        Assert.Equal(
            assistant ? AiStoredMessageRole.Assistant : AiStoredMessageRole.User,
            message.Role);
        Assert.Equal("A private test message", message.Text);
        Assert.Equal(CreatedAtUtc, message.SavedAtUtc);
    }

    [Fact]
    public void Create_DifferentMessagesReceiveDifferentIds()
    {
        var actor = Actor();
        var conversation = Conversation(actor);

        var first = Make(false, conversation, actor);
        var second = Make(false, conversation, actor);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void UserMessage_TrimsOuterWhitespace()
    {
        var actor = Actor();
        var message = Make(false, Conversation(actor), actor,
            text: "  Hello ANDRIL  \n");

        Assert.Equal("Hello ANDRIL", message.Text);
    }

    [Fact]
    public void UserMessage_PreservesInternalNewlinesAndUnicode()
    {
        var actor = Actor();
        var message = Make(false, Conversation(actor), actor,
            text: "  தமிழ்\nසිංහල\nHello  ");

        Assert.Equal("தமிழ்\nසිංහල\nHello", message.Text);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void UserMessage_AsciiCharacterBoundary(int count, bool accepted)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var text = new string('a', count);

        if (accepted)
        {
            Assert.Equal(text, Make(false, conversation, actor, text: text).Text);
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() =>
                Make(false, conversation, actor, text: text));
            Assert.Equal("userText", error.ParamName);
        }
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void UserMessage_EmojiTextElementBoundary(int count, bool accepted)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var text = string.Concat(Enumerable.Repeat("😀", count));

        if (accepted)
        {
            Assert.Equal(text, Make(false, conversation, actor, text: text).Text);
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() =>
                Make(false, conversation, actor, text: text));
            Assert.Equal("userText", error.ParamName);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void UserMessage_NullEmptyOrWhitespaceIsRejected(string? text)
    {
        var actor = Actor();
        var error = Assert.Throws<ArgumentException>(() =>
            Make(false, Conversation(actor), actor, text: text));

        Assert.Equal("userText", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void AssistantMessage_NullEmptyOrWhitespaceIsRejected(string? text)
    {
        var actor = Actor();
        var error = Assert.Throws<ArgumentException>(() =>
            Make(true, Conversation(actor), actor, text: text));

        Assert.Equal("assistantText", error.ParamName);
    }

    [Fact]
    public void AssistantMessage_PreservesResponseWhitespaceAndUnicode()
    {
        var actor = Actor();
        const string reply = "  வணக்கம்\nHello  ";

        var message = Make(true, Conversation(actor), actor, text: reply);

        Assert.Equal(reply, message.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_EmptyOperationIdIsRejected(bool assistant)
    {
        var actor = Actor();
        var error = Assert.Throws<ArgumentException>(() =>
            Make(assistant, Conversation(actor), actor,
                operationId: Guid.Empty));

        Assert.Equal("operationId", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_NullConversationIsRejected(bool assistant)
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            Make(assistant, null!, Actor()));

        Assert.Equal("conversation", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_NullActorIsRejected(bool assistant)
    {
        var conversation = Conversation(Actor());
        var error = Assert.Throws<ArgumentNullException>(() =>
            Make(assistant, conversation, null!));

        Assert.Equal("actor", error.ParamName);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Create_DeniesCrossIdentityOrModuleAccess(
        bool assistant, int mismatch)
    {
        var owner = Actor(AiModule.Learning);
        var conversation = Conversation(owner);
        var other = mismatch switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies }
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            Make(assistant, conversation, other));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void Create_DeniesInvalidActor(bool assistant, int invalidPart)
    {
        var owner = Actor();
        var conversation = Conversation(owner);
        var invalid = invalidPart switch
        {
            0 => owner with { UserId = Guid.Empty },
            1 => owner with { ModuleProfileId = Guid.Empty },
            2 => owner with { Module = (AiModule)0 },
            _ => owner with { Module = (AiModule)99 }
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            Make(assistant, conversation, invalid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ExactlyAtExpiryIsDenied(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);

        Assert.Throws<UnauthorizedAccessException>(() =>
            Make(assistant, conversation, actor, savedAt: ExpiresAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_OneTickBeforeExpiryIsAllowed(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var instant = ExpiresAtUtc.AddTicks(-1);

        var message = Make(assistant, conversation, actor, savedAt: instant);

        Assert.Equal(instant, message.SavedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_AfterConversationDeletionIsDenied(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        conversation.MarkDeleted(CreatedAtUtc.AddMinutes(1));

        Assert.Throws<UnauthorizedAccessException>(() =>
            Make(assistant, conversation, actor,
                savedAt: CreatedAtUtc.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_MessageBeforeConversationCreationIsRejected(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Make(assistant, conversation, actor,
                savedAt: CreatedAtUtc.AddTicks(-1)));

        Assert.Equal("serverSavedAtUtc", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DefaultServerTimestampIsRejected(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);

        var error = Assert.Throws<ArgumentException>(() =>
            Make(assistant, conversation, actor,
                savedAt: default(DateTimeOffset)));

        Assert.Equal("serverSavedAtUtc", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_NormalizesSriLankaTimestampToUtc(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var local = new DateTimeOffset(
            2026, 11, 16, 16, 30, 0, TimeSpan.FromMinutes(330));

        var message = Make(assistant, conversation, actor, savedAt: local);

        Assert.Equal(CreatedAtUtc.AddHours(1), message.SavedAtUtc);
        Assert.Equal(TimeSpan.Zero, message.SavedAtUtc.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DoesNotMutateConversationExpiry(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var expiryBefore = conversation.ExpiresAtUtc;
        var lastUserBefore = conversation.LastSavedUserMessageAtUtc;

        Make(assistant, conversation, actor,
            savedAt: CreatedAtUtc.AddDays(1));

        Assert.Equal(expiryBefore, conversation.ExpiresAtUtc);
        Assert.Equal(lastUserBefore, conversation.LastSavedUserMessageAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToStoredMessage_MapsExactInternalFields(bool assistant)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var message = Make(assistant, conversation, actor,
            text: "Test DTO", savedAt: CreatedAtUtc.AddMinutes(5));

        AiStoredAiMessage dto = message.ToStoredMessage();

        Assert.Equal(message.Id, dto.MessageId);
        Assert.Equal(message.ConversationId, dto.ConversationId);
        Assert.Equal(message.Role, dto.Role);
        Assert.Equal(message.Text, dto.Text);
        Assert.Equal(message.SavedAtUtc, dto.SavedAtUtc);
    }

    [Fact]
    public void UserAndAssistantMessage_CanPreserveSameOperationIdWithDifferentRoles()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();

        var user = Make(false, conversation, actor, operationId);
        var assistant = Make(true, conversation, actor, operationId);

        Assert.Equal(operationId, user.OperationId);
        Assert.Equal(operationId, assistant.OperationId);
        Assert.Equal(AiStoredMessageRole.User, user.Role);
        Assert.Equal(AiStoredMessageRole.Assistant, assistant.Role);
        Assert.NotEqual(user.Id, assistant.Id);
    }

    [Fact]
    public void Factory_DoesNotProvideIdempotentDeduplication()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var operationId = Guid.NewGuid();

        var first = Make(false, conversation, actor, operationId);
        var second = Make(false, conversation, actor, operationId);

        Assert.NotEqual(first.Id, second.Id);
        // This explicitly documents a responsibility of the future repository.
    }

    private static AiAuthorizedActor Actor(
        AiModule module = AiModule.Learning) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static AiConversationEntity Conversation(AiAuthorizedActor actor) =>
        AiConversationEntity.CreateForFirstUserMessage(
            actor, Guid.NewGuid(), CreatedAtUtc);

    private static AiMessageEntity Make(
        bool assistant,
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid? operationId = null,
        string? text = "A synthetic message",
        DateTimeOffset? savedAt = null)
    {
        var id = operationId ?? Guid.NewGuid();
        var instant = savedAt ?? CreatedAtUtc;

        return assistant
            ? AiMessageEntity.CreateAssistantMessage(
                conversation, actor, id, text, instant)
            : AiMessageEntity.CreateUserMessage(
                conversation, actor, id, text, instant);
    }
}
