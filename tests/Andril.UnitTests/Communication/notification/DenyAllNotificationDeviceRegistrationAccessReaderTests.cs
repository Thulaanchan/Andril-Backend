
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class DenyAllNotificationDeviceRegistrationAccessReaderTests
{
    private readonly INotificationDeviceRegistrationAccessReader
        _reader =
            new DenyAllNotificationDeviceRegistrationAccessReader();

    private static NotificationPushRequest CreateRequest(
        NotificationModule module = NotificationModule.Learning,
        Guid? profileId = null,
        Guid? deviceId = null)
    {
        var scope = new NotificationModuleScope(
            module,
            profileId ?? Guid.NewGuid());

        return new NotificationPushRequest(
            Guid.NewGuid(),
            scope,
            deviceId ?? Guid.NewGuid());
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task IsAuthorized_ShouldDenyEveryModule(
        NotificationModule module)
    {
        var result =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                CreateRequest(module));

        Assert.False(result);
    }

    [Fact]
    public async Task IsAuthorized_ShouldDenyEmptyAccountId()
    {
        var result =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.Empty,
                CreateRequest());

        Assert.False(result);
    }

    [Fact]
    public async Task IsAuthorized_ShouldDenyNullRequest()
    {
        var result =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                null!);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAuthorized_ShouldDenyDifferentAccounts()
    {
        var request = CreateRequest(
            NotificationModule.Social);

        var first =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                request);

        var second =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                request);

        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public async Task IsAuthorized_ShouldDenyDifferentProfiles()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var firstRequest = CreateRequest(
            NotificationModule.Hobbies,
            Guid.NewGuid(),
            deviceId);

        var secondRequest = CreateRequest(
            NotificationModule.Hobbies,
            Guid.NewGuid(),
            deviceId);

        var first =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                accountId,
                firstRequest);

        var second =
            await _reader.IsAuthorizedActiveRegistrationAsync(
                accountId,
                secondRequest);

        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public async Task IsAuthorized_ShouldAlwaysDenyRepeatedRequests()
    {
        var accountId = Guid.NewGuid();

        var request = CreateRequest(
            NotificationModule.Social);

        for (var i = 0; i < 5; i++)
        {
            var result =
                await _reader.IsAuthorizedActiveRegistrationAsync(
                    accountId,
                    request);

            Assert.False(result);
        }
    }

    [Fact]
    public void IsAuthorized_ShouldRespectCancellation()
    {
        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                CreateRequest(),
                cancellation.Token);
        });
    }

    [Fact]
    public void IsAuthorized_ShouldCheckCancellationBeforeNullRequest()
    {
        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = _reader.IsAuthorizedActiveRegistrationAsync(
                Guid.NewGuid(),
                null!,
                cancellation.Token);
        });
    }
}
