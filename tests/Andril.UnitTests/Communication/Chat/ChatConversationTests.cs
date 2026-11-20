using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatConversationTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void ValidConversation_ShouldPreserveExactScopeAndParticipants(ChatModule module)
    {
        var conversationId = Guid.NewGuid();
        var participant = Participant(module);

        var conversation = ChatConversation.Create(module, conversationId, [participant]);

        Assert.Equal(module, conversation.Module);
        Assert.Equal(conversationId, conversation.ConversationId);
        Assert.Same(participant, Assert.Single(conversation.Participants));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedModule_ShouldReject(int module)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChatConversation.Create((ChatModule)module, Guid.NewGuid(), []));

        Assert.Equal("module", error.ParamName);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void EmptyConversationId_ShouldReject(ChatModule module)
    {
        var error = Assert.Throws<ArgumentException>(() => ChatConversation.Create(module, Guid.Empty, []));

        Assert.Equal("conversationId", error.ParamName);
    }

    [Fact]
    public void NullParticipants_ShouldReject()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            ChatConversation.Create(ChatModule.Learning, Guid.NewGuid(), null!));

        Assert.Equal("participants", error.ParamName);
    }

    [Fact]
    public void NullParticipantWithinCollection_ShouldReject()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatConversation.Create(ChatModule.Hobbies, Guid.NewGuid(), [Participant(ChatModule.Hobbies), null!]));

        Assert.Equal("participants", error.ParamName);
    }

    public static IEnumerable<object[]> DifferentModules()
    {
        foreach (var conversationModule in Enum.GetValues<ChatModule>())
        {
            foreach (var participantModule in Enum.GetValues<ChatModule>())
            {
                if (conversationModule != participantModule)
                {
                    yield return [conversationModule, participantModule];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(DifferentModules))]
    public void CrossModuleParticipant_ShouldRejectWithoutLeakingIdentifiers(
        ChatModule conversationModule, ChatModule participantModule)
    {
        var conversationId = Guid.NewGuid();
        var participant = Participant(participantModule);

        var error = Assert.Throws<ArgumentException>(() =>
            ChatConversation.Create(conversationModule, conversationId, [Participant(conversationModule), participant]));

        Assert.Equal("participants", error.ParamName);
        AssertNoIdentifiers(error, conversationId, participant);
        Assert.DoesNotContain(participantModule.ToString(), error.Message);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void RepeatedParticipantObject_ShouldReject(ChatModule module)
    {
        var participant = Participant(module);

        Assert.Throws<ArgumentException>(() =>
            ChatConversation.Create(module, Guid.NewGuid(), [participant, participant]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedProfileInSeparateObjects_ShouldRejectEvenWithDifferentAccount(bool changeAccount)
    {
        var conversationId = Guid.NewGuid();
        var first = Participant(ChatModule.Social);
        var second = ChatParticipantReference.Create(first.Module,
            changeAccount ? Guid.NewGuid() : first.AccountId, first.ModuleProfileId);

        var error = Assert.Throws<ArgumentException>(() =>
            ChatConversation.Create(first.Module, conversationId, [first, second]));

        Assert.Equal("participants", error.ParamName);
        AssertNoIdentifiers(error, conversationId, first);
        AssertNoIdentifiers(error, conversationId, second);
    }

    [Fact]
    public void DistinctProfilesForSameAccount_ShouldNotInventAccountEligibilityRule()
    {
        var accountId = Guid.NewGuid();
        var first = ChatParticipantReference.Create(ChatModule.Learning, accountId, Guid.NewGuid());
        var second = ChatParticipantReference.Create(ChatModule.Learning, accountId, Guid.NewGuid());

        var conversation = ChatConversation.Create(ChatModule.Learning, Guid.NewGuid(), [first, second]);

        Assert.Equal(2, conversation.Participants.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ParticipantCount_ShouldNotImplyGroupEligibility(int count)
    {
        var participants = Enumerable.Range(0, count).Select(_ => Participant(ChatModule.Hobbies));

        var conversation = ChatConversation.Create(ChatModule.Hobbies, Guid.NewGuid(), participants);

        Assert.Equal(count, conversation.Participants.Count);
    }

    [Fact]
    public void MutatingSourceArray_ShouldNotChangeSnapshotOrIntroduceAnotherModule()
    {
        var original = Participant(ChatModule.Learning);
        ChatParticipantReference[] participants = [original];
        var conversation = ChatConversation.Create(ChatModule.Learning, Guid.NewGuid(), participants);

        participants[0] = Participant(ChatModule.Social);

        Assert.Same(original, Assert.Single(conversation.Participants));
    }

    [Fact]
    public void MutatingSourceList_ShouldNotChangeSnapshot()
    {
        var original = Participant(ChatModule.Hobbies);
        List<ChatParticipantReference> participants = [original];
        var conversation = ChatConversation.Create(ChatModule.Hobbies, Guid.NewGuid(), participants);

        participants.Clear();
        participants.Add(Participant(ChatModule.Social));

        Assert.Same(original, Assert.Single(conversation.Participants));
    }

    [Fact]
    public void ExposedCollection_ShouldRejectMutationThroughMutableInterface()
    {
        var original = Participant(ChatModule.Social);
        var conversation = ChatConversation.Create(ChatModule.Social, Guid.NewGuid(), [original]);
        var mutableView = Assert.IsAssignableFrom<IList<ChatParticipantReference>>(conversation.Participants);

        Assert.Throws<NotSupportedException>(() => mutableView[0] = Participant(ChatModule.Learning));
        Assert.Throws<NotSupportedException>(() => mutableView.Add(Participant(ChatModule.Social)));
        Assert.Throws<NotSupportedException>(() => mutableView.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
        Assert.Same(original, Assert.Single(conversation.Participants));
    }

    [Fact]
    public void DomainProperties_ShouldNotExposeSettersOrInitBypasses()
    {
        Assert.All(typeof(ChatConversation).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(ChatParticipantReference).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task DraftCreation_ShouldUseExactScopeAndNeverAuthorizeMessaging(ChatModule module)
    {
        var conversation = ChatConversation.Create(module, Guid.NewGuid(), [Participant(module)]);
        const string text = " \tவணக்கம் 👩🏽‍💻\r\nSecond line ";

        var draft = conversation.CreateTextMessageDraft(text);

        Assert.Equal(conversation.Module, draft.Module);
        Assert.Equal(conversation.ConversationId, draft.ConversationId);
        Assert.Equal(text, draft.Text);
        Assert.Null(draft.MoodTone);
        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        Assert.Null(await authorizer.AuthorizeConversationAsync(conversation.Module, conversation.ConversationId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void DraftCreation_ShouldKeepExistingTextRejections(string? text)
    {
        var conversation = ChatConversation.Create(ChatModule.Learning, Guid.NewGuid(), []);

        var error = Assert.ThrowsAny<ArgumentException>(() => conversation.CreateTextMessageDraft(text));

        Assert.Equal("text", error.ParamName);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void DraftCreation_ShouldKeepExistingNonNullMoodToneRejections(ChatModule module)
    {
        var conversation = ChatConversation.Create(module, Guid.NewGuid(), []);

        var error = Assert.Throws<ArgumentException>(() =>
            conversation.CreateTextMessageDraft("Hello", "unapproved-tone"));

        Assert.Equal("moodTone", error.ParamName);
    }

    [Fact]
    public void SameAccountAcrossModules_ShouldKeepSeparateProfileReferences()
    {
        var accountId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var conversations = Enum.GetValues<ChatModule>()
            .Select(module => ChatConversation.Create(module, conversationId,
                [ChatParticipantReference.Create(module, accountId, Guid.NewGuid())]))
            .ToArray();

        Assert.Equal(3, conversations.Select(conversation => conversation.Module).Distinct().Count());
        Assert.Equal(3, conversations.Select(conversation => conversation.Participants[0].ModuleProfileId).Distinct().Count());
        Assert.All(conversations, conversation => Assert.Equal(conversation.Module, conversation.Participants[0].Module));
    }

    private static ChatParticipantReference Participant(ChatModule module) =>
        ChatParticipantReference.Create(module, Guid.NewGuid(), Guid.NewGuid());

    private static void AssertNoIdentifiers(ArgumentException error, Guid conversationId, ChatParticipantReference participant)
    {
        Assert.DoesNotContain(conversationId.ToString(), error.Message);
        Assert.DoesNotContain(participant.AccountId.ToString(), error.Message);
        Assert.DoesNotContain(participant.ModuleProfileId.ToString(), error.Message);
    }
}
