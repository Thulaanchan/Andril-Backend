
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
        var provider = new MockAiResponseProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        var reply = await service.ReplyAsync(
            AiModule.Learning,
            new string('a', 500));

        Assert.True(reply.IsMock);
        Assert.Contains("[MOCK]", reply.Text);
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
        var provider = new MockAiResponseProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Learning)),
            provider);

        var message = string.Concat(
            Enumerable.Repeat("😀", 500));

        var reply = await service.ReplyAsync(
            AiModule.Learning, message);

        Assert.True(reply.IsMock);
        Assert.Contains("[MOCK]", reply.Text);
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
    public async Task PaddedValidMessage_PreservesBuiltInMockBehavior()
    {
        var provider = new MockAiResponseProvider();

        var service = new AiChatService(
            new StubAuthorization(
                AuthorizedActor(AiModule.Hobbies)),
            provider);

        var reply = await service.ReplyAsync(
            AiModule.Hobbies, "  Hello  ");

        var unpadded = await service.ReplyAsync(AiModule.Hobbies, "Hello");
        Assert.Equal(unpadded, reply);
        Assert.True(reply.IsMock);
        Assert.Equal(AiModule.Hobbies, reply.Module);
    }

    [Fact]
    public async Task ArbitraryProviderWithEmptyMockReply_IsRejectedBeforeExecution()
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

        Assert.Equal(0, provider.Calls);
        Assert.Null(provider.LastPrompt);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArbitraryProvider_IsRejectedEvenWhenItClaimsToBeMock(bool claimsMock)
    {
        const string privateText = "private-prompt-do-not-log";
        var actor = AuthorizedActor(AiModule.Learning);
        var provider = new RecordingProvider(new AiProviderResponse("private-provider-reply", claimsMock));
        var authorization = new StubAuthorization(actor);
        var service = new AiChatService(authorization, provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReplyAsync(AiModule.Learning, privateText));

        Assert.Equal(1, authorization.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.Null(provider.LastPrompt);
        Assert.Null(error.InnerException);
        foreach (var value in new[] { privateText, "private-provider-reply", actor.UserId.ToString(), actor.ModuleProfileId.ToString() })
            Assert.DoesNotContain(value, error.ToString());
    }

    [Fact]
    public async Task NullProviderFailsSafelyWithoutLiveOrMockFallback()
    {
        var service = new AiChatService(new StubAuthorization(AuthorizedActor(AiModule.Hobbies)), null!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReplyAsync(AiModule.Hobbies, "private-prompt"));
        Assert.DoesNotContain("private-prompt", error.ToString());
        Assert.Contains("built-in development mock", error.Message);
    }

    [Theory]
    [InlineData("தமிழ்")]
    [InlineData("e\u0301")]
    [InlineData("👩🏽‍💻")]
    [InlineData("සිංහල")]
    public async Task UnicodeTextElementBoundaryIsPreservedWithBuiltInMock(string fragment)
    {
        // Construct exactly 500 text elements even when the fixture fragment contains several.
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(fragment);
        elements.MoveNext();
        var element = elements.GetTextElement();
        var message = string.Concat(Enumerable.Repeat(element, 500));
        Assert.Equal(500, new System.Globalization.StringInfo(message).LengthInTextElements);
        var authorization = new StubAuthorization(AuthorizedActor(AiModule.Social));
        var service = new AiChatService(authorization, new MockAiResponseProvider());
        var reply = await service.ReplyAsync(AiModule.Social, message);
        Assert.True(reply.IsMock);
        Assert.Contains("[MOCK]", reply.Text);
        Assert.DoesNotContain(message, reply.Text);
        await Assert.ThrowsAsync<AiChatValidationException>(() => service.ReplyAsync(AiModule.Social, message + element));
        Assert.Equal(1, authorization.Calls); // Over-limit rejection precedes authorization too.
    }

    [Fact]
    public async Task PreCancellationPrecedesAuthorizationEvenForBuiltInMock()
    {
        var authorization = new StubAuthorization(AuthorizedActor(AiModule.Learning));
        var service = new AiChatService(authorization, new MockAiResponseProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReplyAsync(AiModule.Learning, "Current", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, authorization.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterAsyncAuthorizationPreventsProviderInvocation(bool builtInMock)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AiAuthorizedActor?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new ScriptedAuthorization((module, token) =>
        {
            Assert.Equal(AiModule.Hobbies, module);
            Assert.Equal(cancellation.Token, token);
            entered.SetResult();
            return release.Task; // Deliberately ignores cancellation: service must recheck after await.
        });
        var recording = new RecordingProvider();
        var service = new AiChatService(authorization, builtInMock ? new MockAiResponseProvider() : recording);
        var pending = service.ReplyAsync(AiModule.Hobbies, "private-current", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.SetResult(AuthorizedActor(AiModule.Hobbies));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, recording.Calls);
        Assert.Equal(1, authorization.Calls);
    }

    [Fact]
    public async Task CancellationDuringCooperativeAuthorizationPropagatesWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new ScriptedAuthorization(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AuthorizedActor(AiModule.Learning);
        });
        var provider = new RecordingProvider();
        var service = new AiChatService(authorization, provider);
        var pending = service.ReplyAsync(AiModule.Learning, "current", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, authorization.Calls);
    }

    [Fact]
    public async Task AuthorizationFailureNeverFallsBackToProvider()
    {
        var failure = new InvalidOperationException("Synthetic authorization unavailable");
        var authorization = new ScriptedAuthorization((_, _) => throw failure);
        var provider = new RecordingProvider();
        var service = new AiChatService(authorization, provider);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplyAsync(AiModule.Learning, "current")));
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task BuiltInMockIsSealedDeterministicAndExplicitlyDevelopmentOnly(AiModule module)
    {
        Assert.True(typeof(MockAiResponseProvider).IsSealed);
        var authorization = new StubAuthorization(AuthorizedActor(module));
        var service = new AiChatService(authorization, new MockAiResponseProvider());
        var first = await service.ReplyAsync(module, "private-first");
        var second = await service.ReplyAsync(module, "private-second");
        Assert.Equal(first, second);
        Assert.True(first.IsMock);
        Assert.StartsWith("[MOCK]", first.Text);
        Assert.DoesNotContain("private-", first.Text);
        Assert.Equal(2, authorization.Calls); // No cached actor approval.
    }

    private sealed class ScriptedAuthorization(Func<AiModule, CancellationToken, Task<AiAuthorizedActor?>> authorize)
        : IAiActorAuthorization
    {
        public int Calls { get; private set; }
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        {
            Calls++;
            return authorize(module, cancellationToken);
        }
    }

    private static AiAuthorizedActor AuthorizedActor(
        AiModule module) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private sealed class StubAuthorization(
        AiAuthorizedActor? actor)
        : IAiActorAuthorization
    {
        public int Calls { get; private set; }
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
            AiModule module,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(actor);
        }
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
