using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D15-B offline characterization of the existing push dispatch boundary.
/// The KnownGap tests intentionally reproduce CURRENT unsafe timing behavior.
/// A passing KnownGap test is evidence of a security gap, NOT proof of security.
/// Do not enable production push based on this suite.
/// </summary>
public sealed class NotificationDispatchBoundaryCharacterizationTests
{
    [Fact]
    public async Task RevokedBeforeInitialAccess_DeniesWithoutSending()
    {
        var f = new Fixture();
        f.Access.CurrentScope = null;

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(0, f.Repository.FindCalls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task DeviceDeniedAtFinalCheck_DeniesWithoutSending()
    {
        var f = new Fixture();
        f.Devices.Allowed = false;

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task WrongAuthorizedModule_DeniesBeforeRepositoryLookup()
    {
        var f = new Fixture();
        f.Access.CurrentScope = new NotificationModuleScope(
            NotificationModule.Hobbies, Guid.NewGuid());

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(0, f.Repository.FindCalls);
        Assert.Equal(0, f.Prefs.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task DeviceReaderFailure_ThrowsButNeverCallsPushSender()
    {
        var f = new Fixture();
        f.Devices.BeforeReturn = () =>
            throw new InvalidOperationException("Simulated device outage.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    // KNOWN GAP: profile authorization becomes invalid after the first
    // resolution. The current service does not re-resolve before SendAsync.
    // A PASS demonstrates the gap; after an approved fix, replace this
    // characterization with an assertion that Sender.Calls is zero.
    [Fact]
    [Trait("KnownSecurityGap", "D15-A06")]
    public async Task KnownGap_ProfileRevokedDuringDeviceCheck_StillCallsSender()
    {
        var f = new Fixture();
        f.Devices.BeforeReturn = () => f.Access.CurrentScope = null;

        await f.ExecuteAsync();

        Assert.Null(f.Access.CurrentScope);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(1, f.Sender.Calls);
    }

    // KNOWN GAP: a push preference changes after it has been checked.
    // The current service does not reread it before invoking SendAsync.
    // This test documents current behavior; it does not approve it.
    [Fact]
    [Trait("KnownSecurityGap", "D15-A06")]
    public async Task KnownGap_PushDisabledDuringDeviceCheck_StillCallsSender()
    {
        var f = new Fixture();
        f.Devices.BeforeReturn = () =>
        {
            f.Prefs.Current = new NotificationChannelPreferences(
                f.Scope, true, false, true);
        };

        await f.ExecuteAsync();

        Assert.Equal(1, f.Prefs.Calls);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(1, f.Sender.Calls);
    }

    private sealed class Fixture
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid NotificationId { get; } = Guid.NewGuid();
        public Guid DeviceId { get; } = Guid.NewGuid();
        public NotificationModuleScope Scope { get; } = new(
            NotificationModule.Learning, Guid.NewGuid());

        public FakeAccessReader Access { get; } = new();
        public FakeRepository Repository { get; } = new();
        public FakePreferenceReader Prefs { get; } = new();
        public FakeDeviceReader Devices { get; } = new();
        public FakePushSender Sender { get; } = new();
        private NotificationPushOrchestrationService Service { get; }

        public Fixture()
        {
            Access.CurrentScope = Scope;
            Repository.Entry = new NotificationEntry(
                NotificationId, Scope, "message", "New Message", null,
                DateTimeOffset.UtcNow);
            Prefs.Current = new NotificationChannelPreferences(
                Scope, true, true, true);
            Service = new NotificationPushOrchestrationService(
                Access, Repository, Prefs, Devices, Sender);
        }

        public Task<NotificationPushResult> ExecuteAsync() =>
            Service.TrySendAsync(
                AccountId, NotificationModule.Learning, NotificationId, DeviceId);
    }

    private sealed class FakeAccessReader : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? CurrentScope { get; set; }
        public int Calls { get; private set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid authenticatedAccountId,
            NotificationModule module,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(CurrentScope);
        }
    }

    private sealed class FakeRepository : INotificationRepository
    {
        public NotificationEntry? Entry { get; set; }
        public int FindCalls { get; private set; }

        public Task AddAsync(
            NotificationEntry notification,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
            NotificationModuleScope recipientScope,
            int maxItems,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePreferenceReader : INotificationChannelPreferenceReader
    {
        public NotificationChannelPreferences? Current { get; set; }
        public int Calls { get; private set; }

        public Task<NotificationChannelPreferences?> GetForScopeAsync(
            NotificationModuleScope authorizedScope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Current);
        }
    }

    private sealed class FakeDeviceReader : INotificationDeviceRegistrationAccessReader
    {
        public bool Allowed { get; set; } = true;
        public Action? BeforeReturn { get; set; }
        public int Calls { get; private set; }

        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid authenticatedAccountId,
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforeReturn?.Invoke();
            return Task.FromResult(Allowed);
        }
    }

    private sealed class FakePushSender : INotificationPushSender
    {
        public int Calls { get; private set; }

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                new NotificationPushResult(NotificationPushOutcome.NotSent));
        }
    }
}
