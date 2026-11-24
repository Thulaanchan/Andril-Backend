using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatMessageTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void ValidMessage_ShouldKeepExactModuleConversationAndSender(ChatModule module)
    {
        var f = new Fixture(module);

        var message = f.FromServer();

        Assert.Equal(module, message.Module);
        Assert.Equal(f.MessageId, message.MessageId);
        Assert.Equal(f.Conversation.ConversationId, message.ConversationId);
        Assert.Same(f.Sender, message.Sender);
        Assert.Equal(f.Sender.AccountId, message.Sender.AccountId);
        Assert.Equal(f.Sender.ModuleProfileId, message.Sender.ModuleProfileId);
        Assert.Equal(f.CreatedAt, message.CreatedAtUtc);
        Assert.Null(message.MoodTone);
    }

    [Fact]
    public void ServerCreation_ShouldGenerateDistinctNonEmptyIdsAndUseTheUtcClock()
    {
        var f = new Fixture();
        var before = DateTimeOffset.UtcNow;

        var first = ChatMessage.Create(f.Conversation, f.Sender, "First synthetic message");
        var second = ChatMessage.Create(f.Conversation, f.Sender, "Second synthetic message");

        var after = DateTimeOffset.UtcNow;
        Assert.NotEqual(Guid.Empty, first.MessageId);
        Assert.NotEqual(Guid.Empty, second.MessageId);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.InRange(first.CreatedAtUtc, before, after);
        Assert.InRange(second.CreatedAtUtc, before, after);
        Assert.Equal(TimeSpan.Zero, first.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, second.CreatedAtUtc.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedModule_ShouldRejectThroughExistingDraftValidation(int module)
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => f.FromServer(module: (ChatModule)module));

        Assert.Equal("module", error.ParamName);
    }

    [Fact]
    public void EmptyMessageId_ShouldRejectWithoutReplacement()
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(messageId: Guid.Empty));

        Assert.Equal("messageId", error.ParamName);
    }

    [Fact]
    public void EmptyConversationId_ShouldRejectWithoutReplacement()
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(conversationId: Guid.Empty));

        Assert.Equal("conversationId", error.ParamName);
    }

    [Fact]
    public void MissingConversation_ShouldReject()
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentNullException>(() => ChatMessage.Create(null!, f.Sender, "Hello"));

        Assert.Equal("conversation", error.ParamName);
    }

    [Fact]
    public void MissingSender_ShouldReject()
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentNullException>(() => ChatMessage.Create(f.Conversation, null!, "Hello"));

        Assert.Equal("sender", error.ParamName);
    }

    public static IEnumerable<object[]> DifferentModules()
    {
        foreach (var messageModule in Enum.GetValues<ChatModule>())
        {
            foreach (var otherModule in Enum.GetValues<ChatModule>())
            {
                if (messageModule != otherModule)
                {
                    yield return [messageModule, otherModule];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(DifferentModules))]
    public void CrossModuleSender_ShouldRejectEvenWithIdenticalAccountAndProfileValues(ChatModule module, ChatModule otherModule)
    {
        var f = new Fixture(module);
        var sender = ChatParticipantReference.Create(otherModule, f.Sender.AccountId, f.Sender.ModuleProfileId);

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(sender: sender));

        Assert.Equal("sender", error.ParamName);
        AssertPrivacySafe(error, f);
    }

    [Theory]
    [MemberData(nameof(DifferentModules))]
    public void MessageConversationModuleMismatch_ShouldReject(ChatModule module, ChatModule otherModule)
    {
        var f = new Fixture(module);
        var participant = ChatParticipantReference.Create(otherModule, f.Sender.AccountId, f.Sender.ModuleProfileId);
        var conversation = ChatConversation.Create(otherModule, f.Conversation.ConversationId, [participant]);

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(conversation: conversation));

        Assert.Equal("conversation", error.ParamName);
        AssertPrivacySafe(error, f);
    }

    [Fact]
    public void ConversationIdMismatch_ShouldReject()
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(conversationId: Guid.NewGuid()));

        Assert.Equal("conversation", error.ParamName);
        AssertPrivacySafe(error, f);
    }

    [Theory]
    [InlineData("different-account")]
    [InlineData("different-profile")]
    [InlineData("different-both")]
    [InlineData("no-participants")]
    public void Nonparticipant_ShouldRejectWithoutInferringOwnership(string scenario)
    {
        var f = new Fixture();
        var sender = ChatParticipantReference.Create(f.Conversation.Module,
            scenario is "different-account" or "different-both" ? Guid.NewGuid() : f.Sender.AccountId,
            scenario is "different-profile" or "different-both" ? Guid.NewGuid() : f.Sender.ModuleProfileId);
        var conversation = scenario == "no-participants"
            ? ChatConversation.Create(f.Conversation.Module, f.Conversation.ConversationId, [])
            : f.Conversation;

        var error = Assert.Throws<ArgumentException>(() => f.FromServer(conversation: conversation, sender: sender));

        Assert.Equal("sender", error.ParamName);
        AssertPrivacySafe(error, f);
        Assert.DoesNotContain(sender.AccountId.ToString(), error.Message);
        Assert.DoesNotContain(sender.ModuleProfileId.ToString(), error.Message);
    }

    [Fact]
    public void MatchingReference_ShouldUseCanonicalParticipantFromConversationSnapshot()
    {
        var f = new Fixture();
        var equivalent = ChatParticipantReference.Create(f.Sender.Module, f.Sender.AccountId, f.Sender.ModuleProfileId);

        var message = f.FromServer(sender: equivalent);

        Assert.NotSame(equivalent, message.Sender);
        Assert.Same(f.Sender, message.Sender);
    }

    public static IEnumerable<object[]> ValidTextByModule()
    {
        string[] texts = ["Hello", "வணக்கம் தமிழ்", "😀 👩🏽‍💻 ❤️", "First\nSecond", "First\r\nSecond", " \t e\u0301 😀 \n ", new string('x', 100_000)];
        foreach (var module in Enum.GetValues<ChatModule>())
        {
            foreach (var text in texts)
            {
                yield return [module, text];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ValidTextByModule))]
    public void AcceptedContent_ShouldRemainExactlyConsistentWithStep02(ChatModule module, string text)
    {
        var f = new Fixture(module);
        var draft = f.Conversation.CreateTextMessageDraft(text);

        var message = ChatMessage.Create(f.Conversation, f.Sender, text);

        Assert.Equal(draft.Module, message.Module);
        Assert.Equal(draft.ConversationId, message.ConversationId);
        Assert.Equal(draft.Text, message.Text);
        Assert.Equal(draft.MoodTone, message.MoodTone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0\u2003\u3000")]
    public void InvalidText_ShouldRejectThroughStep02(string? text)
    {
        var f = new Fixture();

        var error = Assert.ThrowsAny<ArgumentException>(() => ChatMessage.Create(f.Conversation, f.Sender, text));

        Assert.Equal("text", error.ParamName);
    }

    [Theory]
    [InlineData(ChatModule.Learning, "unapproved-tone")]
    [InlineData(ChatModule.Learning, "")]
    [InlineData(ChatModule.Hobbies, "unapproved-tone")]
    [InlineData(ChatModule.Hobbies, " ")]
    [InlineData(ChatModule.Social, "unapproved-tone")]
    [InlineData(ChatModule.Social, "")]
    [InlineData(ChatModule.Social, " ")]
    public void NonNullMoodTone_ShouldKeepExistingModuleRestrictions(ChatModule module, string moodTone)
    {
        var f = new Fixture(module);

        var error = Assert.Throws<ArgumentException>(() =>
            ChatMessage.Create(f.Conversation, f.Sender, PrivateText, moodTone));

        Assert.Equal("moodTone", error.ParamName);
        AssertPrivacySafe(error, f);
        if (!string.IsNullOrWhiteSpace(moodTone)) Assert.DoesNotContain(moodTone, error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(-420)]
    public void TrustedTimestamp_ShouldNormalizeOffsetWithoutChangingInstant(int offsetMinutes)
    {
        var f = new Fixture();
        var serverTimestamp = new DateTimeOffset(2030, 2, 3, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));

        var message = f.FromServer(createdAt: serverTimestamp);

        Assert.Equal(TimeSpan.Zero, message.CreatedAtUtc.Offset);
        Assert.Equal(serverTimestamp.UtcDateTime, message.CreatedAtUtc.UtcDateTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrSentinelTimestamp_ShouldReject(bool maximum)
    {
        var f = new Fixture();

        var error = Assert.Throws<ArgumentException>(() =>
            f.FromServer(createdAt: maximum ? DateTimeOffset.MaxValue : default(DateTimeOffset)));

        Assert.Equal("createdAt", error.ParamName);
        AssertPrivacySafe(error, f);
    }

    [Fact]
    public void MessageAndSender_ShouldRemainImmutableAfterCallerChanges()
    {
        var f = new Fixture();
        ChatParticipantReference[] participants = [f.Sender];
        var conversation = ChatConversation.Create(f.Conversation.Module, f.Conversation.ConversationId, participants);
        var text = PrivateText;
        var message = ChatMessage.Create(conversation, f.Sender, text);
        var id = message.MessageId;
        var createdAt = message.CreatedAtUtc;

        participants[0] = ChatParticipantReference.Create(ChatModule.Social, Guid.NewGuid(), Guid.NewGuid());
        text = "Changed local variable";

        Assert.Equal(PrivateText, message.Text);
        Assert.Same(f.Sender, message.Sender);
        Assert.Equal(id, message.MessageId);
        Assert.Equal(createdAt, message.CreatedAtUtc);
        Assert.All(typeof(ChatMessage).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(ChatParticipantReference).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(ChatMessage).GetConstructors());
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task ValidDomainMessage_ShouldNeverGrantMessagingPermission(ChatModule module)
    {
        var f = new Fixture(module);
        var message = ChatMessage.Create(f.Conversation, f.Sender, PrivateText);

        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        Assert.Null(await fallback.AuthorizeConversationAsync(message.Module, message.ConversationId));
        var unresolvedPolicy = new ChatConversationAccessPolicy(null, null);
        Assert.Null(await unresolvedPolicy.AuthorizeConversationAsync(message.Module, message.ConversationId));
    }

    [Fact]
    public void DiagnosticString_ShouldNotExposeTextOrPrivateIdentifiers()
    {
        var f = new Fixture();
        var message = f.FromServer();

        Assert.Equal(nameof(ChatMessage), message.ToString());
        Assert.DoesNotContain(PrivateText, message.ToString());
        Assert.DoesNotContain(message.MessageId.ToString(), message.ToString());
        Assert.DoesNotContain(message.ConversationId.ToString(), message.ToString());
        Assert.DoesNotContain(message.Sender.AccountId.ToString(), message.ToString());
        Assert.DoesNotContain(message.Sender.ModuleProfileId.ToString(), message.ToString());
    }

    private const string PrivateText = "Synthetic private message வணக்கம் 😀";

    private static void AssertPrivacySafe(ArgumentException error, Fixture f)
    {
        Assert.DoesNotContain(f.MessageId.ToString(), error.Message);
        Assert.DoesNotContain(f.Conversation.ConversationId.ToString(), error.Message);
        Assert.DoesNotContain(f.Sender.AccountId.ToString(), error.Message);
        Assert.DoesNotContain(f.Sender.ModuleProfileId.ToString(), error.Message);
        Assert.DoesNotContain(PrivateText, error.Message);
    }

    private sealed class Fixture
    {
        internal Guid MessageId { get; } = Guid.NewGuid();
        internal ChatParticipantReference Sender { get; }
        internal ChatConversation Conversation { get; }
        internal DateTimeOffset CreatedAt { get; } = new(2030, 2, 3, 12, 0, 0, TimeSpan.Zero);

        internal Fixture(ChatModule module = ChatModule.Learning)
        {
            Sender = ChatParticipantReference.Create(module, Guid.NewGuid(), Guid.NewGuid());
            Conversation = ChatConversation.Create(module, Guid.NewGuid(), [Sender]);
        }

        internal ChatMessage FromServer(
            ChatModule? module = null,
            Guid? messageId = null,
            Guid? conversationId = null,
            ChatConversation? conversation = null,
            ChatParticipantReference? sender = null,
            string? text = PrivateText,
            DateTimeOffset? createdAt = null,
            string? moodTone = null) =>
            ChatMessage.CreateFromServerValues(module ?? Conversation.Module, messageId ?? MessageId,
                conversationId ?? Conversation.ConversationId, conversation ?? Conversation, sender ?? Sender,
                text, createdAt ?? CreatedAt, moodTone);
    }
}
