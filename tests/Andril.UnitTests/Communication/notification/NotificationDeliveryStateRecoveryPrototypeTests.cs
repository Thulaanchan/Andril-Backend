using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// ANDRIL M4 D13-B: TEST-ONLY delivery state and recovery simulation.
///
/// The store is shared in-memory state, NOT PostgreSQL or durable storage.
/// Recreating a worker does not simulate an actual process or machine restart.
/// No real authorization, device-token validation, network delivery, RLS,
/// migration, production dependency injection, or provider retry policy.
/// All state transitions and explicit retry eligibility are PROPOSED.
/// </summary>
public sealed class NotificationDeliveryStateRecoveryPrototypeTests
{
    private enum MockDeliveryState
    {
        Pending,
        Claimed,
        TemporaryFailure,
        ProviderAccepted,
        PermanentFailure,
        Unknown,
        Suppressed
    }

    private enum MockProviderOutcome
    {
        ProviderAccepted,
        TemporaryFailure,
        PermanentFailure,
        Unknown
    }

    private sealed record MockEligibility(
        bool ProfileAuthorized,
        bool PushPreferenceEnabled,
        bool DeviceAuthorized)
    {
        internal bool MaySend =>
            ProfileAuthorized && PushPreferenceEnabled && DeviceAuthorized;
    }

    private static readonly MockEligibility Eligible = new(true, true, true);
    private static readonly MockEligibility Revoked = new(false, true, true);
    private static readonly MockEligibility PushDisabled = new(true, false, true);
    private static readonly MockEligibility DeviceDenied = new(true, true, false);

    private sealed record MockSnapshot(
        MockDeliveryState State,
        int AttemptCount,
        Guid? CurrentAttemptId);

    private sealed class MockDeliveryEntry
    {
        internal MockDeliveryEntry(
            Guid notificationId,
            NotificationModuleScope scope,
            Guid deviceRegistrationId)
        {
            NotificationId = notificationId;
            Scope = scope;
            DeviceRegistrationId = deviceRegistrationId;
        }

        internal Guid NotificationId { get; }
        internal NotificationModuleScope Scope { get; }
        internal Guid DeviceRegistrationId { get; }
        internal MockDeliveryState State { get; set; } = MockDeliveryState.Pending;
        internal int AttemptCount { get; set; }
        internal Guid? CurrentAttemptId { get; set; }

        internal MockSnapshot Snapshot() =>
            new(State, AttemptCount, CurrentAttemptId);
    }

    /// <summary>
    /// Models independent delivery state; it never creates a notification.
    /// Locking represents a single-process atomic claim, not DB concurrency.
    /// </summary>
    private sealed class SharedMockDeliveryStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, MockDeliveryEntry> _rows = new();

        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _rows.Count;
                }
            }
        }

        internal bool RegisterPending(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId)
        {
            if (notificationId == Guid.Empty ||
                scope is null ||
                scope.RecipientModuleProfileId == Guid.Empty ||
                !Enum.IsDefined(scope.Module) ||
                deviceRegistrationId == Guid.Empty)
            {
                return false;
            }

            lock (_gate)
            {
                if (_rows.ContainsKey(notificationId))
                {
                    // Never replace an existing delivery identity or scope.
                    return false;
                }

                _rows.Add(notificationId,
                    new MockDeliveryEntry(notificationId, scope,
                        deviceRegistrationId));
                return true;
            }
        }

        private bool TryFindOwned(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId,
            out MockDeliveryEntry? row)
        {
            row = null;
            if (notificationId == Guid.Empty || scope is null ||
                deviceRegistrationId == Guid.Empty ||
                !_rows.TryGetValue(notificationId, out var candidate) ||
                candidate.Scope != scope ||
                candidate.DeviceRegistrationId != deviceRegistrationId)
            {
                return false;
            }

            row = candidate;
            return true;
        }

        internal MockSnapshot? ReadOwned(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId)
        {
            lock (_gate)
            {
                return TryFindOwned(notificationId, scope,
                    deviceRegistrationId, out var row)
                    ? row!.Snapshot()
                    : null;
            }
        }

        internal Guid? TryClaim(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId,
            MockEligibility eligibility,
            bool reviewedRetryPermission = false)
        {
            lock (_gate)
            {
                if (!TryFindOwned(notificationId, scope,
                        deviceRegistrationId, out var row) ||
                    !eligibility.MaySend)
                {
                    return null;
                }

                var newAttemptAllowed =
                    row!.State == MockDeliveryState.Pending ||
                    (row.State == MockDeliveryState.TemporaryFailure &&
                     reviewedRetryPermission);

                if (!newAttemptAllowed)
                {
                    return null;
                }

                row.CurrentAttemptId = Guid.NewGuid();
                row.State = MockDeliveryState.Claimed;
                row.AttemptCount++;
                return row.CurrentAttemptId;
            }
        }

        /// <summary>
        /// Rechecks mock eligibility immediately at the dispatch boundary.
        /// Claiming alone never authorizes a provider call.
        /// </summary>
        internal bool MayDispatch(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId,
            Guid attemptId,
            MockEligibility currentEligibility)
        {
            lock (_gate)
            {
                return attemptId != Guid.Empty &&
                       currentEligibility.MaySend &&
                       TryFindOwned(notificationId, scope,
                           deviceRegistrationId, out var row) &&
                       row!.State == MockDeliveryState.Claimed &&
                       row.CurrentAttemptId == attemptId;
            }
        }

        internal bool TryRecordProviderOutcome(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId,
            Guid attemptId,
            MockProviderOutcome outcome)
        {
            lock (_gate)
            {
                if (attemptId == Guid.Empty ||
                    !Enum.IsDefined(outcome) ||
                    !TryFindOwned(notificationId, scope,
                        deviceRegistrationId, out var row) ||
                    row!.State != MockDeliveryState.Claimed ||
                    row.CurrentAttemptId != attemptId)
                {
                    return false;
                }

                row.State = outcome switch
                {
                    MockProviderOutcome.ProviderAccepted =>
                        MockDeliveryState.ProviderAccepted,
                    MockProviderOutcome.TemporaryFailure =>
                        MockDeliveryState.TemporaryFailure,
                    MockProviderOutcome.PermanentFailure =>
                        MockDeliveryState.PermanentFailure,
                    MockProviderOutcome.Unknown =>
                        MockDeliveryState.Unknown,
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome))
                };
                return true;
            }
        }

        internal bool TrySuppressBeforeDispatch(
            Guid notificationId,
            NotificationModuleScope? scope,
            Guid deviceRegistrationId,
            MockEligibility currentEligibility)
        {
            lock (_gate)
            {
                if (currentEligibility.MaySend ||
                    !TryFindOwned(notificationId, scope,
                        deviceRegistrationId, out var row) ||
                    row!.State is not (
                        MockDeliveryState.Pending or
                        MockDeliveryState.Claimed or
                        MockDeliveryState.TemporaryFailure))
                {
                    return false;
                }

                row.State = MockDeliveryState.Suppressed;
                return true;
            }
        }
    }

    private sealed class MockDeliveryWorker
    {
        private readonly SharedMockDeliveryStore _store;

        internal MockDeliveryWorker(SharedMockDeliveryStore store)
        {
            _store = store;
        }

        internal Guid? Claim(
            Guid notificationId,
            NotificationModuleScope scope,
            Guid deviceRegistrationId,
            MockEligibility eligibility,
            bool reviewedRetryPermission = false) =>
            _store.TryClaim(notificationId, scope, deviceRegistrationId,
                eligibility, reviewedRetryPermission);
    }

    private static NotificationModuleScope Scope(NotificationModule module) =>
        new(module, Guid.NewGuid());

    [Fact]
    public void RegisteringDelivery_ShouldNotAutomaticallyDispatch()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Learning);
        var device = Guid.NewGuid();

        Assert.True(store.RegisterPending(id, scope, device));
        Assert.False(store.RegisterPending(id, scope, device));
        Assert.Equal(1, store.Count);

        var snapshot = Assert.IsType<MockSnapshot>(store.ReadOwned(id, scope, device));
        Assert.Equal(MockDeliveryState.Pending, snapshot.State);
        Assert.Equal(0, snapshot.AttemptCount);
        Assert.Null(snapshot.CurrentAttemptId);
    }

    [Fact]
    public async Task ConcurrentWorkers_ShouldClaimOnlyOneAttempt()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Hobbies);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                new MockDeliveryWorker(store).Claim(id, scope, device, Eligible))));

        var winningAttempt = Assert.Single(claims, attempt => attempt.HasValue);
        Assert.NotNull(winningAttempt);
        var snapshot = Assert.IsType<MockSnapshot>(store.ReadOwned(id, scope, device));
        Assert.Equal(MockDeliveryState.Claimed, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(winningAttempt, snapshot.CurrentAttemptId);
    }

    [Fact]
    public void ForeignModuleProfileOrDevice_ShouldNotReadOrClaim()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var owner = Scope(NotificationModule.Social);
        var foreignModule = Scope(NotificationModule.Hobbies);
        var foreignProfile = Scope(NotificationModule.Social);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, owner, device));

        Assert.Null(store.ReadOwned(id, foreignModule, device));
        Assert.Null(store.ReadOwned(id, foreignProfile, device));
        Assert.Null(store.ReadOwned(id, owner, Guid.NewGuid()));
        Assert.Null(store.TryClaim(id, foreignModule, device, Eligible));
        Assert.Null(store.TryClaim(id, foreignProfile, device, Eligible));
        Assert.Null(store.TryClaim(id, owner, Guid.NewGuid(), Eligible));
        Assert.Equal(0, store.ReadOwned(id, owner, device)!.AttemptCount);
    }

    [Fact]
    public void TemporaryFailure_ShouldRequireExplicitReviewedRetryPermission()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Learning);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));

        var firstAttempt = Assert.IsType<Guid>(
            new MockDeliveryWorker(store).Claim(id, scope, device, Eligible));
        Assert.True(store.TryRecordProviderOutcome(id, scope, device,
            firstAttempt, MockProviderOutcome.TemporaryFailure));
        Assert.Null(store.TryClaim(id, scope, device, Eligible));

        var retryAttempt = store.TryClaim(id, scope, device, Eligible,
            reviewedRetryPermission: true);
        Assert.NotNull(retryAttempt);
        Assert.NotEqual(firstAttempt, retryAttempt);
        Assert.Equal(2, store.ReadOwned(id, scope, device)!.AttemptCount);
        Assert.False(store.TryRecordProviderOutcome(id, scope, device,
            firstAttempt, MockProviderOutcome.ProviderAccepted));
    }

    [Fact]
    public void ProviderAccepted_ShouldNotMeanConfirmedDeviceDeliveryOrPermitResend()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Hobbies);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));
        var attempt = Assert.IsType<Guid>(store.TryClaim(id, scope, device, Eligible));

        Assert.True(store.MayDispatch(id, scope, device, attempt, Eligible));
        Assert.True(store.TryRecordProviderOutcome(id, scope, device,
            attempt, MockProviderOutcome.ProviderAccepted));
        Assert.Equal(MockDeliveryState.ProviderAccepted,
            store.ReadOwned(id, scope, device)!.State);
        Assert.Null(store.TryClaim(id, scope, device, Eligible,
            reviewedRetryPermission: true));
        // Provider acceptance is deliberately not modeled as device delivery.
    }

    [Fact]
    public void PermanentFailure_ShouldNotPermitRetry()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Social);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));
        var attempt = Assert.IsType<Guid>(store.TryClaim(id, scope, device, Eligible));

        Assert.True(store.TryRecordProviderOutcome(id, scope, device,
            attempt, MockProviderOutcome.PermanentFailure));
        Assert.Null(store.TryClaim(id, scope, device, Eligible,
            reviewedRetryPermission: true));
        Assert.Equal(1, store.ReadOwned(id, scope, device)!.AttemptCount);
    }

    [Fact]
    public void UnknownOutcome_AfterWorkerRecreation_ShouldBlockBlindRetry()
    {
        var sharedStore = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Learning);
        var device = Guid.NewGuid();
        Assert.True(sharedStore.RegisterPending(id, scope, device));

        var firstWorker = new MockDeliveryWorker(sharedStore);
        var attempt = Assert.IsType<Guid>(firstWorker.Claim(id, scope, device, Eligible));
        Assert.True(sharedStore.TryRecordProviderOutcome(id, scope, device,
            attempt, MockProviderOutcome.Unknown));

        // New worker, SAME in-memory store; NOT a real process restart.
        var recreatedWorker = new MockDeliveryWorker(sharedStore);
        Assert.Null(recreatedWorker.Claim(id, scope, device, Eligible,
            reviewedRetryPermission: true));
        Assert.Equal(MockDeliveryState.Unknown,
            sharedStore.ReadOwned(id, scope, device)!.State);
        Assert.Equal(1, sharedStore.ReadOwned(id, scope, device)!.AttemptCount);
    }

    [Fact]
    public void UnresolvedClaim_AfterWorkerRecreation_ShouldNotDispatchTwice()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Hobbies);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));
        var first = new MockDeliveryWorker(store);
        var attempt = Assert.IsType<Guid>(first.Claim(id, scope, device, Eligible));

        var restartedWorker = new MockDeliveryWorker(store);
        Assert.Null(restartedWorker.Claim(id, scope, device, Eligible,
            reviewedRetryPermission: true));
        Assert.Equal(attempt, store.ReadOwned(id, scope, device)!.CurrentAttemptId);
        Assert.Equal(1, store.ReadOwned(id, scope, device)!.AttemptCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EligibilityRevokedBeforeDispatch_ShouldFailClosed(int denial)
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Social);
        var device = Guid.NewGuid();
        Assert.True(store.RegisterPending(id, scope, device));
        var attempt = Assert.IsType<Guid>(store.TryClaim(id, scope, device, Eligible));

        var invalidEvidence = denial switch
        {
            0 => Revoked,
            1 => PushDisabled,
            2 => DeviceDenied,
            _ => throw new ArgumentOutOfRangeException(nameof(denial))
        };

        Assert.False(store.MayDispatch(id, scope, device,
            attempt, invalidEvidence));
        Assert.True(store.TrySuppressBeforeDispatch(id, scope, device,
            invalidEvidence));
        Assert.False(store.MayDispatch(id, scope, device, attempt, Eligible));
        Assert.Null(store.TryClaim(id, scope, device, Eligible,
            reviewedRetryPermission: true));
        Assert.Equal(MockDeliveryState.Suppressed,
            store.ReadOwned(id, scope, device)!.State);
    }

    [Fact]
    public void ForeignScope_ShouldNotModifyProviderOutcome()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();

        var owner = Scope(NotificationModule.Learning);
        var foreignModule = Scope(NotificationModule.Hobbies);
        var foreignProfile = Scope(NotificationModule.Learning);

        var device = Guid.NewGuid();
        var wrongDevice = Guid.NewGuid();

        Assert.True(store.RegisterPending(id, owner, device));

        var attempt = Assert.IsType<Guid>(
            store.TryClaim(id, owner, device, Eligible));

        Assert.False(store.TryRecordProviderOutcome(
            id, foreignModule, device, attempt,
            MockProviderOutcome.ProviderAccepted));

        Assert.False(store.TryRecordProviderOutcome(
            id, foreignProfile, device, attempt,
            MockProviderOutcome.ProviderAccepted));

        Assert.False(store.TryRecordProviderOutcome(
            id, owner, wrongDevice, attempt,
            MockProviderOutcome.ProviderAccepted));

        Assert.False(store.TrySuppressBeforeDispatch(
            id, foreignModule, device, Revoked));

        Assert.False(store.TrySuppressBeforeDispatch(
            id, foreignProfile, device, Revoked));

        var snapshot = Assert.IsType<MockSnapshot>(
            store.ReadOwned(id, owner, device));

        Assert.Equal(MockDeliveryState.Claimed, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(attempt, snapshot.CurrentAttemptId);
    }

    [Fact]
    public void InvalidAttemptId_ShouldNotOverwriteDeliveryState()
    {
        var store = new SharedMockDeliveryStore();
        var id = Guid.NewGuid();
        var scope = Scope(NotificationModule.Social);
        var device = Guid.NewGuid();

        Assert.True(store.RegisterPending(id, scope, device));

        var validAttempt = Assert.IsType<Guid>(
            store.TryClaim(id, scope, device, Eligible));

        Assert.False(store.TryRecordProviderOutcome(
            id, scope, device, Guid.Empty,
            MockProviderOutcome.ProviderAccepted));

        Assert.False(store.TryRecordProviderOutcome(
            id, scope, device, Guid.NewGuid(),
            MockProviderOutcome.ProviderAccepted));

        Assert.False(store.TryRecordProviderOutcome(
            id, scope, device, validAttempt,
            (MockProviderOutcome)999));

        var snapshot = Assert.IsType<MockSnapshot>(
            store.ReadOwned(id, scope, device));

        Assert.Equal(MockDeliveryState.Claimed, snapshot.State);
        Assert.Equal(validAttempt, snapshot.CurrentAttemptId);
        Assert.Equal(1, snapshot.AttemptCount);
    }

}
