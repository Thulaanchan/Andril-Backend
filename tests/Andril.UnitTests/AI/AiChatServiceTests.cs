
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Providers;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic-data tests for the AI-BE-01 internal service.
/// No database, HTTP endpoint, real identity, or live AI provider is used.
/// </summary>
public sealed class AiChatServiceTests
{
    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task AuthorizedModule_ReturnsLabeledMockReply(
        AiModule module)
    {
        var service = new AiChatService(
            new StubAuthorization(AuthorizedActor(module)),
            new MockAiResponseProvider());

        var reply = await service.ReplyAsync(
            module, "Hello ANDRIL");

        Assert.Equal(module, reply.Module);
        Assert.True(reply.IsMock);
        Assert.Contains("[MOCK]", reply.Text);
        Assert.Contains(module.ToString(), reply.Text);
    }

    [Fact]
    public async Task DeniedActor_DoesNotCallProvider()
    {
        var provider = new RecordingProvider();
        var service = new AiChatService(
            new StubAuthorization(null), provider);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReplyAsync(
                AiModule.Hobbies, "Test"));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ActorFromAnotherModule_DoesNotCallProvider()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReplyAsync(
                AiModule.Social, "Test"));

        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyActorIdentifier_DoesNotCallProvider(
        bool emptyUserId)
    {
        var provider = new RecordingProvider();

        var actor = new AiAuthorizedActor(
            emptyUserId ? Guid.Empty : Guid.NewGuid(),
            emptyUserId ? Guid.NewGuid() : Guid.Empty,
            AiModule.Learning);

        var service = new AiChatService(
            new StubAuthorization(actor), provider);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ReplyAsync(
                AiModule.Learning, "Test"));

        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public async Task EmptyMessage_IsRejectedBeforeProvider(
        string? message)
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await Assert.ThrowsAsync<AiChatValidationException>(
            () => service.ReplyAsync(
                AiModule.Learning, message));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Exactly500Characters_AreAccepted()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await service.ReplyAsync(
            AiModule.Learning,
            new string('a', 500));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task MoreThan500Characters_AreRejectedBeforeProvider()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await Assert.ThrowsAsync<AiChatValidationException>(
            () => service.ReplyAsync(
                AiModule.Learning,
                new string('a', 501)));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Exactly500EmojiTextElements_AreAccepted()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        var message = string.Concat(
            Enumerable.Repeat("😀", 500));

        await service.ReplyAsync(
            AiModule.Learning, message);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task MoreThan500EmojiTextElements_AreRejected()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        var message = string.Concat(
            Enumerable.Repeat("😀", 501));

        await Assert.ThrowsAsync<AiChatValidationException>(
            () => service.ReplyAsync(
                AiModule.Learning, message));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task UnsupportedModule_IsRejectedBeforeProvider()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await Assert.ThrowsAsync<AiChatValidationException>(
            () => service.ReplyAsync(
                (AiModule)99, "Test"));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Message_IsTrimmedBeforeReachingProvider()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Hobbies)),
            provider);

        await service.ReplyAsync(
            AiModule.Hobbies, "  Hello  ");

        Assert.Equal(1, provider.Calls);
        Assert.Equal("Hello", provider.LastPrompt!.Text);
        Assert.Equal(
            AiModule.Hobbies,
            provider.LastPrompt.Module);
    }

    [Fact]
    public async Task EmptyProviderReply_IsRejected()
    {
        var provider = new RecordingProvider(
            new AiProviderResponse("  ", true));

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReplyAsync(
                AiModule.Learning, "Test"));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotCallProvider()
    {
        var provider = new RecordingProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReplyAsync(
                AiModule.Learning,
                "Test",
                cancellation.Token));

        Assert.Equal(0, provider.Calls);
    }

    private static AiAuthorizedActor AuthorizedActor(
        AiModule module) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private sealed class StubAuthorization(
        AiAuthorizedActor? actor)
        : IAiActorAuthorization
    {
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
            AiModule module,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(actor);
    }

    private sealed class RecordingProvider(
        AiProviderResponse? configuredResponse = null)
        : IAiResponseProvider
    {
        public int Calls { get; private set; }

        public AiProviderPrompt? LastPrompt { get; private set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiProviderPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = prompt;

            return Task.FromResult(
                configuredResponse ??
                new AiProviderResponse(
                    "[MOCK] Test reply",
                    true));
        }
    }
}
