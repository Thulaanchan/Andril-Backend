
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class DenyAllNotificationDeviceTokenLifecycleTests
{
    private readonly INotificationDeviceTokenLifecycle _lifecycle =
        new DenyAllNotificationDeviceTokenLifecycle();

    private const string DummyToken = "dummy-test-token-not-real";

    private static NotificationModuleScope CreateScope(
        NotificationModule module = NotificationModule.Learning)
    {
        return new NotificationModuleScope(
            module,
            Guid.NewGuid());
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task RegisterOrRotate_ShouldDenyEveryModule(
        NotificationModule module)
    {
        var result = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            CreateScope(module),
            Guid.NewGuid(),
            DummyToken);

        Assert.False(result);
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task Revoke_ShouldDenyEveryModule(
        NotificationModule module)
    {
        var result = await _lifecycle.RevokeAsync(
            Guid.NewGuid(),
            CreateScope(module),
            Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task RegisterOrRotate_ShouldDenyEmptyAccountId()
    {
        var result = await _lifecycle.RegisterOrRotateAsync(
            Guid.Empty,
            CreateScope(),
            Guid.NewGuid(),
            DummyToken);

        Assert.False(result);
    }

    [Fact]
    public async Task RegisterOrRotate_ShouldDenyEmptyDeviceId()
    {
        var result = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            CreateScope(),
            Guid.Empty,
            DummyToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Revoke_ShouldDenyEmptyAccountId()
    {
        var result = await _lifecycle.RevokeAsync(
            Guid.Empty,
            CreateScope(),
            Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task Revoke_ShouldDenyEmptyDeviceId()
    {
        var result = await _lifecycle.RevokeAsync(
            Guid.NewGuid(),
            CreateScope(),
            Guid.Empty);

        Assert.False(result);
    }

    [Fact]
    public async Task RegisterOrRotate_ShouldDenyNullScope()
    {
        var result = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            null!,
            Guid.NewGuid(),
            DummyToken);

        Assert.False(result);
    }

    [Fact]
    public async Task Revoke_ShouldDenyNullScope()
    {
        var result = await _lifecycle.RevokeAsync(
            Guid.NewGuid(),
            null!,
            Guid.NewGuid());

        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterOrRotate_ShouldDenyMissingToken(
        string? token)
    {
        var result = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            CreateScope(),
            Guid.NewGuid(),
            token!);

        Assert.False(result);
    }

    [Fact]
    public async Task RepeatedOperations_ShouldAlwaysDeny()
    {
        var accountId = Guid.NewGuid();
        var scope = CreateScope();
        var deviceId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            var registered =
                await _lifecycle.RegisterOrRotateAsync(
                    accountId,
                    scope,
                    deviceId,
                    DummyToken);

            var revoked = await _lifecycle.RevokeAsync(
                accountId,
                scope,
                deviceId);

            Assert.False(registered);
            Assert.False(revoked);
        }
    }

    [Fact]
    public async Task DifferentAccountsAndProfiles_ShouldRemainDenied()
    {
        var deviceId = Guid.NewGuid();

        var first = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            CreateScope(NotificationModule.Learning),
            deviceId,
            DummyToken);

        var second = await _lifecycle.RegisterOrRotateAsync(
            Guid.NewGuid(),
            CreateScope(NotificationModule.Hobbies),
            deviceId,
            DummyToken);

        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public void RegisterOrRotate_ShouldRespectCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = _lifecycle.RegisterOrRotateAsync(
                Guid.NewGuid(),
                CreateScope(),
                Guid.NewGuid(),
                DummyToken,
                cancellation.Token);
        });
    }

    [Fact]
    public void Revoke_ShouldRespectCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = _lifecycle.RevokeAsync(
                Guid.NewGuid(),
                CreateScope(),
                Guid.NewGuid(),
                cancellation.Token);
        });
    }

    [Fact]
    public void Cancellation_ShouldTakePrecedenceOverInvalidInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = _lifecycle.RegisterOrRotateAsync(
                Guid.Empty,
                null!,
                Guid.Empty,
                null!,
                cancellation.Token);
        });
    }
}
