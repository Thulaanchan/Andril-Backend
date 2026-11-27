using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Providers;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiChatReleaseAuthorizationTests
{
    private sealed class Authority(Func<int, CancellationToken, Task<AiAuthorizedActor?>> read) : IAiActorAuthorization
    {
        internal int Calls;
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken ct = default) => read(++Calls, ct);
    }
    [Theory]
    [InlineData("revoked")]
    [InlineData("foreign-account")]
    [InlineData("foreign-profile")]
    [InlineData("foreign-module")]
    [InlineData("empty-account")]
    public async Task ReplyIsNotReleasedAfterRevocationOrScopeChange(string change)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        AiAuthorizedActor? changed = change switch
        {
            "revoked" => null,
            "foreign-account" => actor with { UserId = Guid.NewGuid() },
            "foreign-profile" => actor with { ModuleProfileId = Guid.NewGuid() },
            "foreign-module" => actor with { Module = AiModule.Hobbies },
            _ => actor with { UserId = Guid.Empty }
        };
        var authority = new Authority((call, _) => Task.FromResult(call == 1 ? actor : changed));
        var service = new AiChatService(authority, new MockAiResponseProvider());
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReplyAsync(AiModule.Learning, "SYNTHETIC-PRIVATE"));
        Assert.Equal(2, authority.Calls); Assert.DoesNotContain("SYNTHETIC", error.ToString()); Assert.DoesNotContain(actor.UserId.ToString(), error.ToString());
    }
    [Fact]
    public async Task SameCurrentValueScopeAcrossDifferentObjectsCanReceiveMockReply()
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var authority = new Authority((_, _) => Task.FromResult<AiAuthorizedActor?>(actor with { })); var service = new AiChatService(authority, new MockAiResponseProvider());
        Assert.True((await service.ReplyAsync(AiModule.Learning, "SYNTHETIC")).IsMock); Assert.Equal(2, authority.Calls);
    }
    [Fact]
    public async Task CancellationDuringReleaseAuthorizationPropagatesWithoutReply()
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning); using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authority = new Authority(async (call, ct) => { if (call == 2) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); } return actor; });
        var service = new AiChatService(authority, new MockAiResponseProvider()); var pending = service.ReplyAsync(AiModule.Learning, "SYNTHETIC", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cts.Token, error.CancellationToken); Assert.Equal(2, authority.Calls);
    }
}
