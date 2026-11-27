using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationPushAuthorizationPropagationTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task AuthorizedDispatch_PropagatesAccountModuleAndExactScope(
        NotificationModule module)
    {
        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var deviceRegistrationId = Guid.NewGuid();
        var scope = new NotificationModuleScope(
            module,
            Guid.NewGuid());
        var access = new RecordingAccessReader(scope);
        var repository = new RecordingRepository(
            new NotificationEntry(
                notificationId,
                scope,
                "message",
                "New Message",
                "Private preview excluded from push request.",
                CreatedAtUtc));
        var preferences = new RecordingPreferenceReader(
            new NotificationChannelPreferences(
                scope,
                inAppEnabled: true,
                pushEnabled: true,
                emailEnabled: false));
        var devices = new RecordingDeviceReader(authorized: true);
        var sender = new RecordingPushSender(
            new NotificationPushResult(
                NotificationPushOutcome.ProviderAccepted));
        var service = new NotificationPushOrchestrationService(
            access,
            repository,
            preferences,
            devices,
            sender);

        var result = await service.TrySendAsync(
            accountId,
            module,
            notificationId,
            deviceRegistrationId);

        Assert.Equal(accountId, access.LastAccountId);
        Assert.Equal(module, access.LastModule);
        Assert.Equal(scope, repository.LastScope);
        Assert.Equal(scope, preferences.LastScope);
        Assert.Equal(accountId, devices.LastAccountId);
        Assert.Equal(notificationId, devices.LastRequest?.NotificationId);
        Assert.Equal(scope, devices.LastRequest?.RecipientScope);
        Assert.Equal(
            deviceRegistrationId,
            devices.LastRequest?.DeviceRegistrationId);
        Assert.Equal(1, sender.Calls);
        Assert.Equal(notificationId, sender.LastRequest?.NotificationId);
        Assert.Equal(scope, sender.LastRequest?.RecipientScope);
        Assert.Equal(
            deviceRegistrationId,
            sender.LastRequest?.DeviceRegistrationId);
        Assert.Equal(
            NotificationPushOutcome.ProviderAccepted,
            result.Outcome);
        Assert.True(result.WasAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedAuthorizedScope_DeniesBeforeDispatch(
        bool returnMismatchedModule)
    {
        var requestedModule = NotificationModule.Learning;
        NotificationModuleScope? resolvedScope = returnMismatchedModule
            ? new NotificationModuleScope(
                NotificationModule.Hobbies,
                Guid.NewGuid())
            : null;
        var access = new RecordingAccessReader(resolvedScope);
        var repository = new RecordingRepository(entry: null);
        var preferences = new RecordingPreferenceReader(value: null);
        var devices = new RecordingDeviceReader(authorized: false);
        var sender = new RecordingPushSender(
            new NotificationPushResult(
                NotificationPushOutcome.ProviderAccepted));
        var service = new NotificationPushOrchestrationService(
            access,
            repository,
            preferences,
            devices,
            sender);

        var result = await service.TrySendAsync(
            Guid.NewGuid(),
            requestedModule,
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.False(result.WasAccepted);
        Assert.Equal(0, repository.Calls);
        Assert.Equal(0, preferences.Calls);
        Assert.Equal(0, devices.Calls);
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAtAuthorizationGate_PreventsSenderInvocation(
        int cancelAtGate)
    {
        using var cancellation = new CancellationTokenSource();
        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var deviceRegistrationId = Guid.NewGuid();
        var scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());
        var access = new RecordingAccessReader(
            scope,
            cancellation,
            cancelAtGate == 0);
        var repository = new RecordingRepository(
            new NotificationEntry(
                notificationId,
                scope,
                "message",
                "New Message",
                null,
                CreatedAtUtc),
            cancellation,
            cancelAtGate == 1);
        var preferences = new RecordingPreferenceReader(
            new NotificationChannelPreferences(
                scope,
                inAppEnabled: true,
                pushEnabled: true,
                emailEnabled: false),
            cancellation,
            cancelAtGate == 2);
        var devices = new RecordingDeviceReader(authorized: true);
        var sender = new RecordingPushSender(
            new NotificationPushResult(
                NotificationPushOutcome.ProviderAccepted));
        var service = new NotificationPushOrchestrationService(
            access,
            repository,
            preferences,
            devices,
            sender);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TrySendAsync(
                accountId,
                NotificationModule.Hobbies,
                notificationId,
                deviceRegistrationId,
                cancellation.Token));

        Assert.Equal(0, devices.Calls);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task CanceledDeviceAuthorizationTask_WithActiveCallerToken_ShouldPropagateCancellationWithoutSending()
    {
        using var caller = new CancellationTokenSource();
        using var deviceCancellation = new CancellationTokenSource();
        deviceCancellation.Cancel();
        var deviceTask = Task.FromCanceled<bool>(deviceCancellation.Token);
        var accountId = Guid.Parse("35000000-0000-0000-0000-000000000001");
        var notificationId = Guid.Parse("35000000-0000-0000-0000-000000000002");
        var deviceId = Guid.Parse("35000000-0000-0000-0000-000000000003");
        var scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.Parse("35000000-0000-0000-0000-000000000004"));
        var access = new RecordingAccessReader(scope);
        var repository = new RecordingRepository(
            new NotificationEntry(notificationId, scope, "message",
                "New Message", null, CreatedAtUtc));
        var preferences = new RecordingPreferenceReader(
            new NotificationChannelPreferences(scope,
                inAppEnabled: true, pushEnabled: true, emailEnabled: false));
        var devices = new RecordingDeviceReader(authorized: true)
        {
            AuthorizationTaskOverride = deviceTask
        };
        var sender = new RecordingPushSender(
            new NotificationPushResult(NotificationPushOutcome.ProviderAccepted));
        var service = new NotificationPushOrchestrationService(
            access, repository, preferences, devices, sender);
        var timeout = TimeSpan.FromSeconds(10);
        Task<NotificationPushResult>? operation = null;

        try
        {
            Assert.True(caller.Token.CanBeCanceled);
            Assert.False(caller.IsCancellationRequested);
            Assert.NotEqual(caller.Token, deviceCancellation.Token);
            Assert.Equal(TaskStatus.Canceled, deviceTask.Status);

            operation = service.TrySendAsync(accountId,
                NotificationModule.Hobbies, notificationId, deviceId, caller.Token);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(timeout));

            Assert.Equal(TaskStatus.Canceled, operation.Status);
            Assert.Equal(deviceCancellation.Token, exception.CancellationToken);
            Assert.Equal(TaskStatus.Canceled, deviceTask.Status);
            Assert.False(caller.IsCancellationRequested);
            Assert.Equal(1, access.Calls);
            Assert.Equal(accountId, access.LastAccountId);
            Assert.Equal(NotificationModule.Hobbies, access.LastModule);
            Assert.Equal(1, repository.Calls);
            Assert.Equal(scope, repository.LastScope);
            Assert.Equal(1, preferences.Calls);
            Assert.Equal(scope, preferences.LastScope);
            Assert.Equal(1, devices.Calls);
            Assert.Equal(accountId, devices.LastAccountId);
            Assert.NotNull(devices.LastRequest);
            Assert.Equal(notificationId, devices.LastRequest.NotificationId);
            Assert.Equal(deviceId, devices.LastRequest.DeviceRegistrationId);
            Assert.Equal(scope, devices.LastRequest.RecipientScope);
            Assert.Equal(caller.Token, devices.LastCancellationToken);
            Assert.False(devices.LastCancellationToken.IsCancellationRequested);
            Assert.Equal(0, sender.Calls);
        }
        finally
        {
            if (operation is not null)
            {
                try
                {
                    await operation.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    // Observe eventual faults without hiding the original failure.
                    _ = operation.ContinueWith(
                        static completed => { _ = completed.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                            TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch
                {
                    // Observe cancellation/fault without replacing an assertion.
                }
            }
        }
    }

    private sealed class RecordingAccessReader(
        NotificationModuleScope? scope,
        CancellationTokenSource? cancellation = null,
        bool cancelWhenCalled = false)
        : INotificationRecipientAccessReader
    {
        public int Calls { get; private set; }

        public Guid LastAccountId { get; private set; }

        public NotificationModule LastModule { get; private set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid authenticatedAccountId,
            NotificationModule module,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastAccountId = authenticatedAccountId;
            LastModule = module;

            if (cancelWhenCalled)
            {
                cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(scope);
        }
    }

    private sealed class RecordingRepository(
        NotificationEntry? entry,
        CancellationTokenSource? cancellation = null,
        bool cancelWhenCalled = false)
        : INotificationRepository
    {
        public int Calls { get; private set; }

        public NotificationModuleScope? LastScope { get; private set; }

        public Task AddAsync(
            NotificationEntry notification,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastScope = recipientScope;

            if (cancelWhenCalled)
            {
                cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(entry);
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

    private sealed class RecordingPreferenceReader(
        NotificationChannelPreferences? value,
        CancellationTokenSource? cancellation = null,
        bool cancelWhenCalled = false)
        : INotificationChannelPreferenceReader
    {
        public int Calls { get; private set; }

        public NotificationModuleScope? LastScope { get; private set; }

        public Task<NotificationChannelPreferences?> GetForScopeAsync(
            NotificationModuleScope authorizedScope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastScope = authorizedScope;

            if (cancelWhenCalled)
            {
                cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(value);
        }
    }

    private sealed class RecordingDeviceReader(bool authorized)
        : INotificationDeviceRegistrationAccessReader
    {
        public int Calls { get; private set; }

        public Guid LastAccountId { get; private set; }

        public NotificationPushRequest? LastRequest { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<bool>? AuthorizationTaskOverride { get; set; }

        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid authenticatedAccountId,
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastAccountId = authenticatedAccountId;
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            return AuthorizationTaskOverride ?? Task.FromResult(authorized);
        }
    }

    private sealed class RecordingPushSender(
        NotificationPushResult result)
        : INotificationPushSender
    {
        public int Calls { get; private set; }

        public NotificationPushRequest? LastRequest { get; private set; }

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
