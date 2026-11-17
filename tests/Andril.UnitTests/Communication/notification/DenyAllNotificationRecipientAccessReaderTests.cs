
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class DenyAllNotificationRecipientAccessReaderTests
{
    private readonly INotificationRecipientAccessReader _reader =
        new DenyAllNotificationRecipientAccessReader();

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task ResolveOwnedActiveScopeAsync_ShouldDenyAllModules(
        NotificationModule module)
    {
        var accountId = Guid.NewGuid();

        var result = await _reader.ResolveOwnedActiveScopeAsync(
            accountId,
            module);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveOwnedActiveScopeAsync_ShouldDenyEmptyAccountId()
    {
        var result = await _reader.ResolveOwnedActiveScopeAsync(
            Guid.Empty,
            NotificationModule.Learning);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveOwnedActiveScopeAsync_ShouldDenyInvalidModule()
    {
        var accountId = Guid.NewGuid();

        var result = await _reader.ResolveOwnedActiveScopeAsync(
            accountId,
            (NotificationModule)999);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveOwnedActiveScopeAsync_ShouldRemainDeniedOnRepeatedCalls()
    {
        var accountId = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            var result = await _reader.ResolveOwnedActiveScopeAsync(
                accountId,
                NotificationModule.Social);

            Assert.Null(result);
        }
    }

    [Fact]
    public async Task ResolveOwnedActiveScopeAsync_ShouldRespectCancellation()
    {
        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await _reader.ResolveOwnedActiveScopeAsync(
                    Guid.NewGuid(),
                    NotificationModule.Social,
                    cancellationSource.Token);
            });
    }
}
