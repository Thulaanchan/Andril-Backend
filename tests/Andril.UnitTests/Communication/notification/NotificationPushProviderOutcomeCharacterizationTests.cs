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
/// D16-A offline push outcome characterization.
/// Uses test-only dependencies and never contacts a provider.
/// Provider acceptance is NOT confirmed device delivery.
/// These tests do not establish production dispatch safety.
/// </summary>
public sealed class NotificationPushProviderOutcomeCharacterizationTests
{
    [Theory]
    [InlineData(NotificationPushOutcome.NotSent)]
    [InlineData(NotificationPushOutcome.ProviderAccepted)]
    [InlineData(NotificationPushOutcome.TemporaryFailure)]
    [InlineData(NotificationPushOutcome.PermanentFailure)]
    public async Task ProviderOutcome_IsPreserved(
        NotificationPushOutcome outcome)
    {
        var f = new Fixture();
        f.Sender.Outcome = outcome;

        var result = await f.ExecuteAsync();

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(
            outcome == NotificationPushOutcome.ProviderAccepted,
            result.WasAccepted);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task NullProviderResult_ReturnsNotSent()
    {
        var f = new Fixture();
        f.Sender.ReturnNull = true;

        var result = await f.ExecuteAsync();

        Assert.Equal(NotificationPushOutcome.NotSent, result.Outcome);
        Assert.False(result.WasAccepted);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task ProviderException_DoesNotReportAcceptance()
    {
        var f = new Fixture();
        f.Sender.ThrowFailure = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.ExecuteAsync());

        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task SenderRequest_PreservesResolvedPrivateScope()
    {
        var f = new Fixture();

        await f.ExecuteAsync();

        var request = Assert.IsType<NotificationPushRequest>(
            f.Sender.CapturedRequest);

        Assert.Equal(f.NotificationId, request.NotificationId);
        Assert.Equal(f.DeviceId, request.DeviceRegistrationId);
        Assert.Equal(f.Scope, request.RecipientScope);
    }

    [Fact]
    public async Task ProviderCancellation_DoesNotReportSuccess()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();

        f.Sender.BeforeResponse = token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.ExecuteAsync(cts.Token));

        Assert.Equal(1, f.Sender.Calls);
    }

    private sealed class Fixture
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid NotificationId { get; } = Guid.NewGuid();
        public Guid DeviceId { get; } = Guid.NewGuid();

        public NotificationModuleScope Scope { get; } =
            new(NotificationModule.Learning, Guid.NewGuid());

        public TestAccess Access { get; } = new();
        public TestRepository Repository { get; } = new();
        public TestPreferences Preferences { get; } = new();
        public TestDevices Devices { get; } = new();
        public TestSender Sender { get; } = new();

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

        public Task<NotificationPushResult> ExecuteAsync(
            CancellationToken token = default)
        {
            return _service.TrySendAsync(
                AccountId,
                NotificationModule.Learning,
                NotificationId,
                DeviceId,
                token);
        }
    }

    private sealed class TestAccess : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? Scope { get; set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid accountId,
            NotificationModule module,
            CancellationToken token = default)
        {
            return Task.FromResult(Scope);
        }
    }

    private sealed class TestRepository : INotificationRepository
    {
        public NotificationEntry? Entry { get; set; }

        public Task AddAsync(
            NotificationEntry entry,
            CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope scope,
            Guid id,
            CancellationToken token = default)
        {
            return Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
            NotificationModuleScope scope,
            int maxItems,
            CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope scope,
            Guid id,
            DateTimeOffset readAtUtc,
            CancellationToken token = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestPreferences : INotificationChannelPreferenceReader
    {
        public NotificationChannelPreferences? Current { get; set; }

        public Task<NotificationChannelPreferences?> GetForScopeAsync(
            NotificationModuleScope scope,
            CancellationToken token = default)
        {
            return Task.FromResult(Current);
        }
    }

    private sealed class TestDevices : INotificationDeviceRegistrationAccessReader
    {
        public Task<bool> IsAuthorizedActiveRegistrationAsync(
            Guid accountId,
            NotificationPushRequest request,
            CancellationToken token = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class TestSender : INotificationPushSender
    {
        public int Calls { get; private set; }

        public NotificationPushOutcome Outcome { get; set; } =
            NotificationPushOutcome.ProviderAccepted;

        public bool ReturnNull { get; set; }
        public bool ThrowFailure { get; set; }

        public Action<CancellationToken>? BeforeResponse { get; set; }

        public NotificationPushRequest? CapturedRequest { get; private set; }

        public Task<NotificationPushResult> SendAsync(
            NotificationPushRequest request,
            CancellationToken token = default)
        {
            Calls++;
            CapturedRequest = request;

            BeforeResponse?.Invoke(token);

            if (ThrowFailure)
                throw new InvalidOperationException(
                    "Simulated provider failure.");

            if (ReturnNull)
                return Task.FromResult<NotificationPushResult>(null!);

            return Task.FromResult(
                new NotificationPushResult(Outcome));
        }
    }
}