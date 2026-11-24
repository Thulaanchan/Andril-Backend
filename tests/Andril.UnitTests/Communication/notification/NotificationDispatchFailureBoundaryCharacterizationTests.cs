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
/// D16-C2 offline failure-boundary characterization.
///
/// These tests demonstrate CURRENT behavior.
/// They do not approve production exception mapping,
/// logging, authorization, retry or provider integration.
/// </summary>
public sealed class NotificationDispatchFailureBoundaryCharacterizationTests
{
    [Fact]
    public async Task AccessUnavailable_PropagatesWithoutDispatch()
    {
        var f = new Fixture();
        f.Access.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(0, f.Repository.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task PreferencesUnavailable_PropagatesWithoutDispatch()
    {
        var f = new Fixture();
        f.Preferences.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(1, f.Repository.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task DeviceAuthorizationUnavailable_PropagatesWithoutDispatch()
    {
        var f = new Fixture();
        f.Devices.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    // Characterizes exception propagation only.
    // This does NOT prove that the API or logs expose
    // provider exception text to a real user.
    [Fact]
    [Trait("ContractLimitation", "ProviderExceptionPropagation")]
    public async Task SyntheticProviderDiagnostic_PropagatesUpstream()
    {
        var f = new Fixture();
        f.Sender.Fail = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Contains(
            "TEST_ONLY_PROVIDER_FAILURE",
            ex.Message);

        Assert.Equal(1, f.Sender.Calls);
    }

    // The result type permits undefined numeric values.
    // This test documents orchestration pass-through;
    // it does not represent an actual provider response.
    [Fact]
    [Trait("ContractLimitation", "UnrecognizedOutcome")]
    public async Task UndefinedOutcome_IsReturnedWithoutNormalization()
    {
        var f = new Fixture();

        var undefined = (NotificationPushOutcome)1234;
        f.Sender.Outcome = undefined;

        var result = await f.ExecuteAsync();

        Assert.False(Enum.IsDefined(
            typeof(NotificationPushOutcome),
            result.Outcome));

        Assert.Equal(undefined, result.Outcome);
        Assert.False(result.WasAccepted);
        Assert.Equal(1, f.Sender.Calls);
    }

    private sealed class Fixture
    {
        private readonly Guid _accountId = Guid.NewGuid();
        private readonly Guid _notificationId = Guid.NewGuid();
        private readonly Guid _deviceId = Guid.NewGuid();

        private readonly NotificationPushOrchestrationService _service;

        public NotificationModuleScope Scope { get; } = new(
            NotificationModule.Learning,
            Guid.NewGuid());

        public FakeAccess Access { get; } = new();
        public FakeRepository Repository { get; } = new();
        public FakePreferences Preferences { get; } = new();
        public FakeDevices Devices { get; } = new();
        public FakeSender Sender { get; } = new();

        public Fixture()
        {
            Access.Scope = Scope;

            Repository.Entry = new NotificationEntry(
                _notificationId,
                Scope,
                "message",
                "Test notification",
                null,
                DateTimeOffset.UtcNow);

            Preferences.Current = new NotificationChannelPreferences(
                Scope,
                true,
                true,
                true);

            _service = new NotificationPushOrchestrationService(
                Access,
                Repository,
                Preferences,
                Devices,
                Sender);
        }

        public Task<NotificationPushResult> ExecuteAsync() =>
            _service.TrySendAsync(
                _accountId,
                NotificationModule.Learning,
                _notificationId,
                _deviceId);
    }

    private sealed class FakeAccess : INotificationRecipientAccessReader
    {
        public bool Fail { get; set; }
        public NotificationModuleScope? Scope { get; set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid accountId,
            NotificationModule module,
            CancellationToken token = default)
        {
            if (Fail)
                throw new InvalidOperationException(
                    "TEST_ONLY_ACCESS_UNAVAILABLE");

            return Task.FromResult(Scope);
        }
    }

    private sealed class FakeRepository : INotificationRepository
    {
        public int Calls { get; private set; }
        public NotificationEntry? Entry { get; set; }

        public Task AddAsync(
            NotificationEntry entry,
            CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope scope,
            Guid id,
            CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
            NotificationModuleScope scope,
            int maxItems,
            CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope scope,
            Guid id,
            DateTimeOffset readAtUtc,
            CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePreferences : INotificationChannelPreferenceReader
    {
        public bool Fail { get; set; }
        public NotificationChannelPreferences? Current { get; set; }

        public Task<NotificationChannelPreferences?> GetForScopeAsync(
            NotificationModuleScope scope,
            CancellationToken token = default)
        {
            if (Fail)
                throw new InvalidOperationException(
                    "TEST_ONLY_PREFERENCES_UNAVAILABLE");

            return Task.FromResult(Current);
        }
    }

    private sealed class FakeDevices : INotificationDeviceRegistrationAccessReader
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid accountId,
            NotificationPushRequest request,
            CancellationToken token = default)
        {
            Calls++;

            if (Fail)
                throw new InvalidOperationException(
                    "TEST_ONLY_DEVICE_UNAVAILABLE");

            return Task.FromResult(true);
        }
    }

    private sealed class FakeSender : INotificationPushSender
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public NotificationPushOutcome Outcome { get; set; } =
            NotificationPushOutcome.NotSent;

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken token = default)
        {
            Calls++;

            if (Fail)
                throw new InvalidOperationException(
                    "TEST_ONLY_PROVIDER_FAILURE");

            return Task.FromResult(
                new NotificationPushResult(Outcome));
        }
    }
}