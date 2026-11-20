using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatParticipantReferenceTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void ValidReference_ShouldKeepAccountAndProfileSeparate(ChatModule module)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var participant = ChatParticipantReference.Create(module, accountId, profileId);

        Assert.Equal(module, participant.Module);
        Assert.Equal(accountId, participant.AccountId);
        Assert.Equal(profileId, participant.ModuleProfileId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedModule_ShouldReject(int module)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChatParticipantReference.Create((ChatModule)module, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal("module", error.ParamName);
    }

    [Fact]
    public void EmptyAccount_ShouldRejectWithoutSubstitutingProfile()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatParticipantReference.Create(ChatModule.Learning, Guid.Empty, Guid.NewGuid()));

        Assert.Equal("accountId", error.ParamName);
    }

    [Fact]
    public void EmptyProfile_ShouldRejectWithoutSubstitutingAccount()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatParticipantReference.Create(ChatModule.Hobbies, Guid.NewGuid(), Guid.Empty));

        Assert.Equal("moduleProfileId", error.ParamName);
    }

    [Fact]
    public async Task EqualIdentifierValues_ShouldNotProveIdentityOrAuthorization()
    {
        var arbitraryId = Guid.NewGuid();
        var participant = ChatParticipantReference.Create(ChatModule.Social, arbitraryId, arbitraryId);
        var conversation = ChatConversation.Create(ChatModule.Social, arbitraryId, [participant]);

        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        Assert.Null(await authorizer.AuthorizeConversationAsync(conversation.Module, conversation.ConversationId));
    }

    [Fact]
    public void DiagnosticString_ShouldNotDisclosePrivateIdentifiers()
    {
        var participant = ChatParticipantReference.Create(ChatModule.Social, Guid.NewGuid(), Guid.NewGuid());

        Assert.DoesNotContain(participant.AccountId.ToString(), participant.ToString()!);
        Assert.DoesNotContain(participant.ModuleProfileId.ToString(), participant.ToString()!);
    }
}
