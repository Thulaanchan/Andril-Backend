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
/// D15-G additional offline boundary characterization.
/// Tests run against the current orchestration service
/// using in-memory fakes.
///
/// KnownSecurityGap tests intentionally demonstrate
/// unresolved behavior, not a production security fix.
/// </summary>
public sealed class NotificationDispatchAdditionalBoundaryCharacterizationTests
{
    [Fact]
    public async Task MissingNotification_DeniesBeforeDeviceAndSender()
    {
        var f = new Fixture();
        f.Repository.Entry = null;

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(0, f.Prefs.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task ForeignProfileNotification_DeniesBeforePreferencesAndSender()
    {
        var f = new Fixture();

        var foreignScope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        f.Repository.Entry = new NotificationEntry(
            f.NotificationId,
            foreignScope,
            "message",
            "New Message",
            null,
            DateTimeOffset.UtcNow);

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(0, f.Prefs.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task MissingPreferenceEvidence_DeniesBeforeDeviceAndSender()
    {
        var f = new Fixture();
        f.Prefs.Current = null;

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Prefs.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task RepositoryUnavailable_DoesNotCallSender()
    {
        var f = new Fixture();
        f.Repository.ThrowOnFind = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(0, f.Prefs.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    // KNOWN GAP: device state changes after the
    // authorization decision but before SendAsync.
    // The existing service has no final device recheck.
    [Fact]
    [Trait("KnownSecurityGap", "D15-A06")]
    public async Task KnownGap_DeviceInvalidatedAfterDecision_StillCallsSender()
    {
        var f = new Fixture();

        f.Devices.AfterDecision = () =>
            f.Devices.Allowed = false;

        await f.ExecuteAsync();

        Assert.False(f.Devices.Allowed);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(1, f.Sender.Calls);
    }

    // KNOWN GAP: cancellation occurs during device
    // validation. The fake sender deliberately ignores
    // cancellation, exposing the lack of a final check.
    [Fact]
    [Trait("KnownSecurityGap", "D15-A06")]
    public async Task KnownGap_CancelledBeforeSender_StillMayInvokeSender()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();

        f.Devices.AfterDecision = cancellation.Cancel;

        await f.ExecuteAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(1, f.Sender.Calls);
    }

    // KNOWN GAP: the current service does not claim a
    // durable delivery attempt. Two sequential requests
    // can each invoke the sender. This does not simulate
    // real PostgreSQL concurrency or external delivery.
    [Fact]
    [Trait("KnownSecurityGap", "D15-A06")]
    public async Task KnownGap_RepeatedRequest_CallsSenderTwice()
    {
        var f = new Fixture();

        await f.ExecuteAsync();
        await f.ExecuteAsync();

        Assert.Equal(2, f.Access.Calls);
        Assert.Equal(2, f.Repository.FindCalls);
        Assert.Equal(2, f.Devices.Calls);
        Assert.Equal(2, f.Sender.Calls);
    }

    private sealed class Fixture
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid NotificationId { get; } = Guid.NewGuid();
        public Guid DeviceId { get; } = Guid.NewGuid();

        public NotificationModuleScope Scope { get; } = new(
            NotificationModule.Learning,
            Guid.NewGuid());

        public FakeAccess Access { get; } = new();
        public FakeRepository Repository { get; } = new();
        public FakePreferences Prefs { get; } = new();
        public FakeDevices Devices { get; } = new();
        public FakeSender Sender { get; } = new();

        private readonly NotificationPushOrchestrationService _service;

        public Fixture()
        {
            Access.Scope = Scope;

            Repository.Entry = new NotificationEntry(
                NotificationId,
                Scope,
                "message",
                "New Message",
                null,
                DateTimeOffset.UtcNow);

            Prefs.Current = new NotificationChannelPreferences(
                Scope,
                true,
                true,
                true);

            _service = new NotificationPushOrchestrationService(
                Access,
                Repository,
                Prefs,
                Devices,
                Sender);
        }

        public Task<NotificationPushResult> ExecuteAsync(
            CancellationToken cancellationToken = default) =>
            _service.TrySendAsync(
                AccountId,
                NotificationModule.Learning,
                NotificationId,
                DeviceId,
                cancellationToken);
    }

    private sealed class FakeAccess : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? Scope { get; set; }
        public int Calls { get; private set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid accountId,
            NotificationModule module,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Scope);
        }
    }

    private sealed class FakeRepository : INotificationRepository
    {
        public NotificationEntry? Entry { get; set; }
        public bool ThrowOnFind { get; set; }
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

            if (ThrowOnFind)
                throw new InvalidOperationException(
                    "Offline simulated repository failure.");

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

    private sealed class FakePreferences : INotificationChannelPreferenceReader
    {
        public NotificationChannelPreferences? Current { get; set; }
        public int Calls { get; private set; }

        public Task<NotificationChannelPreferences?> GetForScopeAsync(
            NotificationModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Current);
        }
    }

    private sealed class FakeDevices : INotificationDeviceRegistrationAccessReader
    {
        public bool Allowed { get; set; } = true;
        public Action? AfterDecision { get; set; }
        public int Calls { get; private set; }

        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid accountId,
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            var authorized = Allowed;
            AfterDecision?.Invoke();

            return Task.FromResult(authorized);
        }
    }

    private sealed class FakeSender : INotificationPushSender
    {
        public int Calls { get; private set; }

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(
                new NotificationPushResult(
                    NotificationPushOutcome.NotSent));
        }
    }
}