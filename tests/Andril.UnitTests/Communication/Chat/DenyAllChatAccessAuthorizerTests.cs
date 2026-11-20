using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class DenyAllChatAccessAuthorizerTests
{
    private readonly IChatAccessAuthorizer _authorizer =
        new DenyAllChatAccessAuthorizer();

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task EveryModuleWithoutActorEvidence_ShouldDeny(ChatModule module)
    {
        var result = await _authorizer.AuthorizeConversationAsync(module, Guid.NewGuid());

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task UnknownModule_ShouldDeny(int module)
    {
        var result = await _authorizer.AuthorizeConversationAsync((ChatModule)module, Guid.NewGuid());

        Assert.Null(result);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task MissingConversation_ShouldDenyWithoutDisclosingActor(ChatModule module)
    {
        var result = await _authorizer.AuthorizeConversationAsync(module, Guid.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task SameIdentifierAcrossModules_ShouldNeverImplyAuthorization()
    {
        var untrustedIdentifier = Guid.NewGuid();

        foreach (var module in Enum.GetValues<ChatModule>())
        {
            // A UUID, even if copied from an account/profile/conversation, proves nothing.
            var result = await _authorizer.AuthorizeConversationAsync(module, untrustedIdentifier);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task RepeatedCalls_ShouldRemainDeniedWithoutExternalDependencies()
    {
        var conversationId = Guid.NewGuid();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await _authorizer.AuthorizeConversationAsync(ChatModule.Social, conversationId);

            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    [InlineData((ChatModule)0)]
    public async Task Cancellation_ShouldPropagateEvenForInvalidTargets(ChatModule module)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _authorizer.AuthorizeConversationAsync(module, Guid.Empty, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }
}
