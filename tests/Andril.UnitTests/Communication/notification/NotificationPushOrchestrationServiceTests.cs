using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationPushOrchestrationServiceTests
{
    private static (
        NotificationPushOrchestrationService Service,
        FakeAccessReader Access,
        FakeRepository Repository,
        FakePreferenceReader Preferences,
        FakeDeviceReader Devices,
        FakePushSender Sender)
        CreateFixture()
    {
        var access = new FakeAccessReader();
        var repository = new FakeRepository();
        var preferences = new FakePreferenceReader();
        var devices = new FakeDeviceReader();
        var sender = new FakePushSender();

        var service = new NotificationPushOrchestrationService(
            access,
            repository,
            preferences,
            devices,
            sender);

        return (
            service,
            access,
            repository,
            preferences,
            devices,
            sender);
    }

    private static void AssertNoDownstreamCalls(
        FakeRepository repository,
        FakePreferenceReader preferences,
        FakeDeviceReader devices,
        FakePushSender sender)
    {
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, preferences.Calls);
        Assert.Equal(0, devices.Calls);
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidIdentifiers_ShouldDenyBeforeAccess(
        int invalidField)
    {
        var f = CreateFixture();

        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        switch (invalidField)
        {
            case 0:
                accountId = Guid.Empty;
                break;

            case 1:
                notificationId = Guid.Empty;
                break;

            case 2:
                deviceId = Guid.Empty;
                break;
        }

        var result = await f.Service.TrySendAsync(
            accountId,
            NotificationModule.Learning,
            notificationId,
            deviceId);

        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        Assert.False(result.WasAccepted);
        Assert.Equal(0, f.Access.Calls);

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task MissingAuthorizedScope_ShouldNeverSend(
        NotificationModule module)
    {
        var f = CreateFixture();

        var result = await f.Service.TrySendAsync(
            Guid.NewGuid(),
            module,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        Assert.False(result.WasAccepted);
        Assert.Equal(1, f.Access.Calls);

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Fact]
    public async Task InvalidModule_ShouldDenyBeforeAccess()
    {
        var f = CreateFixture();

        var result = await f.Service.TrySendAsync(
            Guid.NewGuid(),
            (NotificationModule)999,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        Assert.False(result.WasAccepted);
        Assert.Equal(0, f.Access.Calls);

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Fact]
    public async Task DifferentModuleScope_ShouldDeny()
    {
        var f = CreateFixture();

        f.Access.Scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());

        var result = await f.Service.TrySendAsync(
            Guid.NewGuid(),
            NotificationModule.Learning,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Fact]
    public async Task MissingNotification_ShouldNeverSend()
    {
        var f = CreateFixture();

        var scope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        f.Access.Scope = scope;

        var notificationId = Guid.NewGuid();

        var result = await f.Service.TrySendAsync(
            Guid.NewGuid(),
            NotificationModule.Learning,
            notificationId,
            Guid.NewGuid());

        Assert.Equal(
            NotificationPushOutcome.NotSent,
            result.Outcome);

        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(notificationId, f.Repository.LastId);
        Assert.Equal(scope, f.Repository.LastScope);

        Assert.Equal(0, f.Preferences.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task AuthorizationFailure_ShouldNotSend()
    {
        var f = CreateFixture();

        f.Access.ThrowOnResolve = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.TrySendAsync(
                Guid.NewGuid(),
                NotificationModule.Learning,
                Guid.NewGuid(),
                Guid.NewGuid()));

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Fact]
    public async Task Cancellation_ShouldStopBeforeAccess()
    {
        var f = CreateFixture();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Service.TrySendAsync(
                Guid.NewGuid(),
                NotificationModule.Learning,
                Guid.NewGuid(),
                Guid.NewGuid(),
                cancellation.Token));

        Assert.Equal(0, f.Access.Calls);

        AssertNoDownstreamCalls(
            f.Repository,
            f.Preferences,
            f.Devices,
            f.Sender);
    }

    [Fact]
    public async Task Cancellation_ShouldPrecedeInvalidInput()
    {
        var f = CreateFixture();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Service.TrySendAsync(
                Guid.Empty,
                NotificationModule.Learning,
                Guid.Empty,
                Guid.Empty,
                cancellation.Token));

        Assert.Equal(0, f.Access.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public void Constructor_ShouldRejectMissingDependencies()
    {
        var f = CreateFixture();

        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushOrchestrationService(
                null!,
                f.Repository,
                f.Preferences,
                f.Devices,
                f.Sender));

        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushOrchestrationService(
                f.Access,
                null!,
                f.Preferences,
                f.Devices,
                f.Sender));

        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushOrchestrationService(
                f.Access,
                f.Repository,
                null!,
                f.Devices,
                f.Sender));

        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushOrchestrationService(
                f.Access,
                f.Repository,
                f.Preferences,
                null!,
                f.Sender));

        Assert.Throws<ArgumentNullException>(() =>
            new NotificationPushOrchestrationService(
                f.Access,
                f.Repository,
                f.Preferences,
                f.Devices,
                null!));
    }

    // Step 15K-E: exercise authorized paths beyond initial denial checks.
    private static (
        Guid AccountId,
        Guid NotificationId,
        Guid DeviceId,
        NotificationModuleScope Scope)
        PrepareAuthorizedPath(
            FakeAccessReader access,
            FakeRepository repository,
            FakePreferenceReader preferences,
            FakeDeviceReader devices)
    {
        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var scope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        access.Scope = scope;
        repository.Entry = new NotificationEntry(
            notificationId,
            scope,
            "message",
            "New Message",
            null,
            DateTimeOffset.UtcNow);

        preferences.Value = new NotificationChannelPreferences(
            scope,
            true,
            true,
            true);

        devices.Authorized = true;
        return (accountId, notificationId, deviceId, scope);
    }

    [Fact]
    public async Task WrongNotificationId_ShouldDeny()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            Guid.NewGuid(),
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(0, f.Preferences.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task CrossProfileNotification_ShouldDeny()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        var otherScope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        f.Repository.Entry = new NotificationEntry(
            test.NotificationId,
            otherScope,
            "message",
            "New Message",
            null,
            DateTimeOffset.UtcNow);

        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(0, f.Preferences.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task MissingPreferences_ShouldDeny()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        f.Preferences.Value = null;
        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Preferences.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task CrossProfilePreferences_ShouldDeny()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        var otherScope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());

        f.Preferences.Value = new NotificationChannelPreferences(
            otherScope,
            true,
            true,
            true);

        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferenceScopeMismatch_ShouldDenyBeforeDeviceAndSender(
        bool differentModule)
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);
        var preferenceScope = differentModule
            ? new NotificationModuleScope(NotificationModule.Hobbies,
                test.Scope.RecipientModuleProfileId)
            : new NotificationModuleScope(NotificationModule.Learning,
                Guid.NewGuid());
        f.Preferences.Value = new NotificationChannelPreferences(
            preferenceScope, inAppEnabled: true, pushEnabled: true,
            emailEnabled: true);
        var cancellationToken = CancellationToken.None;

        Assert.NotEqual(Guid.Empty, test.AccountId);
        Assert.NotEqual(Guid.Empty, test.NotificationId);
        Assert.NotEqual(Guid.Empty, test.DeviceId);
        Assert.Equal(NotificationModule.Learning, test.Scope.Module);
        Assert.Equal(test.Scope, f.Access.Scope);
        Assert.False(f.Access.ThrowOnResolve);
        Assert.Equal(test.NotificationId, f.Repository.Entry!.Id);
        Assert.Equal(test.Scope, f.Repository.Entry.RecipientScope);
        Assert.True(f.Preferences.Value.PushEnabled);
        Assert.True(f.Devices.Authorized);
        Assert.False(cancellationToken.IsCancellationRequested);

        if (differentModule)
        {
            Assert.NotEqual(test.Scope.Module, preferenceScope.Module);
            Assert.Equal(test.Scope.RecipientModuleProfileId,
                preferenceScope.RecipientModuleProfileId);
        }
        else
        {
            Assert.Equal(test.Scope.Module, preferenceScope.Module);
            Assert.NotEqual(test.Scope.RecipientModuleProfileId,
                preferenceScope.RecipientModuleProfileId);
        }

        var result = await f.Service.TrySendAsync(test.AccountId,
            NotificationModule.Learning, test.NotificationId, test.DeviceId,
            cancellationToken);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.False(result.WasAccepted);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(test.Scope, f.Repository.LastScope);
        Assert.Equal(test.NotificationId, f.Repository.LastId);
        Assert.Equal(1, f.Preferences.Calls);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task PushDisabled_ShouldNeverSend()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        f.Preferences.Value = new NotificationChannelPreferences(
            test.Scope,
            true,
            false,
            true);

        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(0, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task UnauthorizedDevice_ShouldNeverSend()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        f.Devices.Authorized = false;
        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task EligiblePath_ShouldInvokeSender()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        var result = await f.Service.TrySendAsync(
            test.AccountId,
            NotificationModule.Learning,
            test.NotificationId,
            test.DeviceId);

        Assert.Equal(1, f.Repository.FindCalls);
        Assert.Equal(1, f.Preferences.Calls);
        Assert.Equal(1, f.Devices.Calls);
        Assert.Equal(1, f.Sender.Calls);
        Assert.Same(f.Sender.Result, result);
        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
    }

    [Fact]
    public async Task SenderFailure_ShouldNotReturnFalseSuccess()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);

        f.Sender.ThrowOnSend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.TrySendAsync(
                test.AccountId,
                NotificationModule.Learning,
                test.NotificationId,
                test.DeviceId));

        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task PendingNotificationLookup_WhenFaulted_ShouldBlockDownstreamProcessing()
    {
        var f = CreateFixture();
        var test = PrepareAuthorizedPath(
            f.Access, f.Repository, f.Preferences, f.Devices);
        var lookup = new TaskCompletionSource<NotificationEntry?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        f.Repository.LookupTaskOverride = lookup.Task;
        f.Repository.LookupEntered = entered;
        var timeout = TimeSpan.FromSeconds(10);
        Task<NotificationPushResult>? operation = null;

        try
        {
            Assert.True(f.Preferences.Value!.PushEnabled);
            Assert.Equal(test.Scope, f.Preferences.Value.RecipientScope);
            Assert.True(f.Devices.Authorized);
            Assert.Equal(test.NotificationId, f.Repository.Entry!.Id);
            Assert.Equal(test.Scope, f.Repository.Entry.RecipientScope);
            operation = f.Service.TrySendAsync(test.AccountId,
                NotificationModule.Learning, test.NotificationId,
                test.DeviceId, CancellationToken.None);

            // The signal follows the fake's counter/scope/ID recording.
            // The timeout guards a hang; it does not prove ordering.
            await entered.Task.WaitAsync(timeout);
            Assert.Equal(1, f.Access.Calls);
            Assert.Equal(1, f.Repository.FindCalls);
            Assert.Equal(test.Scope, f.Repository.LastScope);
            Assert.Equal(test.NotificationId, f.Repository.LastId);
            Assert.False(lookup.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, f.Preferences.Calls);
            Assert.Equal(0, f.Devices.Calls);
            Assert.Equal(0, f.Sender.Calls);

            var failure = new InvalidOperationException("N33 synthetic lookup failure.");
            lookup.SetException(failure);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => operation.WaitAsync(timeout));

            Assert.Same(failure, thrown);
            Assert.Equal(1, f.Repository.FindCalls);
            Assert.Equal(0, f.Preferences.Calls);
            Assert.Equal(0, f.Devices.Calls);
            Assert.Equal(0, f.Sender.Calls);
        }
        finally
        {
            // Settle pending signals even if Phase A fails, then observe both
            // tasks without replacing the original assertion failure.
            lookup.TrySetCanceled();
            entered.TrySetCanceled();
            foreach (var pending in new Task?[] { lookup.Task, operation })
            {
                if (pending is null)
                    continue;

                try
                {
                    await pending.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    // A broken implementation may ignore lookup completion.
                    // Observe any eventual fault after the bounded cleanup.
                    _ = pending.ContinueWith(
                        static completed => { _ = completed.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                            TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch
                {
                    // Expected lookup/operation fault or cleanup cancellation.
                }
            }
        }
    }

    private sealed class FakeAccessReader
        : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? Scope { get; set; }

        public bool ThrowOnResolve { get; set; }

        public int Calls { get; private set; }

        public Task<NotificationModuleScope?>
            ResolveOwnedActiveScopeAsync(
                Guid authenticatedAccountId,
                NotificationModule module,
                CancellationToken cancellationToken = default)
        {
            Calls++;

            if (ThrowOnResolve)
            {
                throw new InvalidOperationException(
                    "Simulated authorization failure.");
            }

            return Task.FromResult(Scope);
        }
    }

    private sealed class FakeRepository
        : INotificationRepository
    {
        public int FindCalls { get; private set; }

        public Guid LastId { get; private set; }
        public NotificationEntry? Entry { get; set; }

        public NotificationModuleScope? LastScope { get; private set; }

        public Task<NotificationEntry?>? LookupTaskOverride { get; set; }

        public TaskCompletionSource? LookupEntered { get; set; }

        public Task AddAsync(
            NotificationEntry notification,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            LastScope = recipientScope;
            LastId = notificationId;
            LookupEntered?.TrySetResult();

            return LookupTaskOverride ?? Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<NotificationEntry>>
            ListLatestAsync(
                NotificationModuleScope recipientScope,
                int maxItems,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakePreferenceReader
        : INotificationChannelPreferenceReader
    {
        public int Calls { get; private set; }
        public NotificationChannelPreferences? Value { get; set; }

        public Task<NotificationChannelPreferences?>
            GetForScopeAsync(
                NotificationModuleScope authorizedScope,
                CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(Value);
        }
    }

    private sealed class FakeDeviceReader
        : INotificationDeviceRegistrationAccessReader
    {
        public int Calls { get; private set; }
        public bool Authorized { get; set; }

        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid authenticatedAccountId,
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(Authorized);
        }
    }

    private sealed class FakePushSender
        : INotificationPushSender
    {
        public int Calls { get; private set; }
        public bool ThrowOnSend { get; set; }
        public NotificationPushResult Result { get; set; } =
            new(NotificationPushOutcome.NotSent);

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ThrowOnSend)
            {
                throw new InvalidOperationException(
                    "Simulated push provider failure.");
            }

            return Task.FromResult(Result);
        }
    }
}

