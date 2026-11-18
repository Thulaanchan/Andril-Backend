
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class DenyAllNotificationPushSenderTests
{
    private static NotificationPushRequest CreateRequest(
        NotificationModule module = NotificationModule.Learning)
    {
        var scope = new NotificationModuleScope(
            module,
            Guid.NewGuid());

        return new NotificationPushRequest(
            Guid.NewGuid(),
            scope,
            Guid.NewGuid());
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task SendAsync_ShouldNotSendForAnyModule(
        NotificationModule module)
    {
        INotificationPushSender sender =
            new DenyAllNotificationPushSender();

        var request = CreateRequest(module);

        var result = await sender.SendAsync(request);

        Assert.NotNull(result);
        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        Assert.False(result.WasAccepted);
    }

    [Fact]
    public void SendAsync_ShouldRejectNullRequest()
    {
        var sender = new DenyAllNotificationPushSender();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = sender.SendAsync(null!);
        });
    }

    [Fact]
    public void SendAsync_ShouldRespectCancellation()
    {
        var sender = new DenyAllNotificationPushSender();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = sender.SendAsync(
                CreateRequest(),
                cancellation.Token);
        });
    }

    [Fact]
    public void SendAsync_ShouldCheckCancellationBeforeNull()
    {
        var sender = new DenyAllNotificationPushSender();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = sender.SendAsync(
                null!,
                cancellation.Token);
        });
    }

    [Fact]
    public async Task RepeatedRequests_ShouldAlwaysReturnNotSent()
    {
        var sender = new DenyAllNotificationPushSender();

        var request = CreateRequest(
            NotificationModule.Social);

        for (int i = 0; i < 5; i++)
        {
            var result = await sender.SendAsync(request);

            Assert.Equal(
                NotificationPushOutcome.NotSent,
                result.Outcome);

            Assert.False(result.WasAccepted);
        }
    }

    [Fact]
    public void PushRequest_ShouldPreserveIdentifiers()
    {
        var notificationId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            profileId);

        var request = new NotificationPushRequest(
            notificationId,
            scope,
            deviceId);

        Assert.Equal(
            notificationId,
            request.NotificationId);

        Assert.Equal(
            deviceId,
            request.DeviceRegistrationId);

        Assert.Equal(
            scope,
            request.RecipientScope);

        Assert.Equal(
            NotificationModule.Hobbies,
            request.RecipientScope.Module);

        Assert.Equal(
            profileId,
            request.RecipientScope.RecipientModuleProfileId);
    }

    [Fact]
    public void PushRequest_ShouldRejectEmptyNotificationId()
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            new NotificationPushRequest(
                Guid.Empty,
                scope,
                Guid.NewGuid()));
    }

    [Fact]
    public void PushRequest_ShouldRejectNullRecipientScope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushRequest(
                Guid.NewGuid(),
                null!,
                Guid.NewGuid()));
    }

    [Fact]
    public void PushRequest_ShouldRejectEmptyDeviceId()
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            new NotificationPushRequest(
                Guid.NewGuid(),
                scope,
                Guid.Empty));
    }

    [Fact]
    public void NotSentResult_ShouldNotIndicateAcceptance()
    {
        var result = new NotificationPushResult(
            NotificationPushOutcome.NotSent);

        Assert.False(result.WasAccepted);
    }
}
