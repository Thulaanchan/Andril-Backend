
using System;
using System.Collections.Generic;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D10-E: TEST-ONLY notification delivery retry prototype.
///
/// Separates mock notification persistence from
/// simulated external provider delivery attempts.
///
/// No PostgreSQL, Firebase, network, or production DI.
/// All retry behavior is PROPOSED, not approved policy.
/// </summary>
public sealed class NotificationDeliveryRetryPrototypeTests
{
    private enum ProviderObservation
    {
        Accepted,
        TemporaryFailure,
        PermanentFailure,
        Ambiguous
    }

    private sealed class DeliveryRecord
    {
        public NotificationModuleScope Scope { get; }
        public int Attempts { get; set; }
        public ProviderObservation? LastOutcome { get; set; }

        public DeliveryRecord(NotificationModuleScope scope)
        {
            Scope = scope;
        }
    }

    private sealed class MockDeliveryCoordinator
    {
        private readonly object _gate = new();

        private readonly Dictionary<
            (Guid EventId, NotificationModule Module, Guid ProfileId),
            Guid> _notifications = new();

        private readonly Dictionary<Guid, DeliveryRecord>
            _deliveries = new();

        public int NotificationCount
        {
            get
            {
                lock (_gate)
                {
                    return _notifications.Count;
                }
            }
        }

        public Guid PersistOnce(
            Guid eventId,
            NotificationModuleScope scope)
        {
            if (eventId == Guid.Empty)
                throw new ArgumentException(nameof(eventId));

            ArgumentNullException.ThrowIfNull(scope);

            var key = (
                eventId,
                scope.Module,
                scope.RecipientModuleProfileId);

            lock (_gate)
            {
                if (_notifications.TryGetValue(
                    key, out var existing))
                {
                    return existing;
                }

                var notificationId = Guid.NewGuid();

                _notifications.Add(key, notificationId);
                _deliveries.Add(
                    notificationId,
                    new DeliveryRecord(scope));

                return notificationId;
            }
        }

        public bool RecordDeliveryAttempt(
            Guid notificationId,
            NotificationModuleScope scope,
            ProviderObservation observation)
        {
            lock (_gate)
            {
                if (!_deliveries.TryGetValue(
                    notificationId, out var record))
                {
                    return false;
                }

                if (record.Scope != scope)
                {
                    return false;
                }

                // Proposed safety behavior:
                // Never blindly retry accepted, permanent
                // failure, or ambiguous provider outcomes.
                if (record.LastOutcome is
                    ProviderObservation.Accepted or
                    ProviderObservation.PermanentFailure or
                    ProviderObservation.Ambiguous)
                {
                    return false;
                }

                record.Attempts++;
                record.LastOutcome = observation;

                return true;
            }
        }

        public int GetAttemptCount(Guid notificationId)
        {
            lock (_gate)
            {
                return _deliveries[notificationId].Attempts;
            }
        }

        public ProviderObservation? GetLastOutcome(
            Guid notificationId)
        {
            lock (_gate)
            {
                return _deliveries[notificationId].LastOutcome;
            }
        }
    }

    private static NotificationModuleScope Scope(
        NotificationModule module)
    {
        return new NotificationModuleScope(
            module,
            Guid.NewGuid());
    }

    [Fact]
    public void DuplicateEvent_ShouldNotAutomaticallySendPush()
    {
        var mock = new MockDeliveryCoordinator();
        var scope = Scope(NotificationModule.Learning);
        var eventId = Guid.NewGuid();

        var first = mock.PersistOnce(eventId, scope);
        var repeated = mock.PersistOnce(eventId, scope);

        Assert.Equal(first, repeated);
        Assert.Equal(1, mock.NotificationCount);
        Assert.Equal(0, mock.GetAttemptCount(first));
    }

    [Fact]
    public void TemporaryFailure_ShouldAllowExplicitRetry()
    {
        var mock = new MockDeliveryCoordinator();
        var scope = Scope(NotificationModule.Hobbies);

        var id = mock.PersistOnce(Guid.NewGuid(), scope);

        Assert.True(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.TemporaryFailure));

        Assert.True(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Accepted));

        Assert.Equal(1, mock.NotificationCount);
        Assert.Equal(2, mock.GetAttemptCount(id));
    }

    [Fact]
    public void AmbiguousOutcome_ShouldBlockBlindRetry()
    {
        var mock = new MockDeliveryCoordinator();
        var scope = Scope(NotificationModule.Social);

        var id = mock.PersistOnce(Guid.NewGuid(), scope);

        Assert.True(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Ambiguous));

        Assert.False(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Accepted));

        Assert.Equal(1, mock.GetAttemptCount(id));
        Assert.Equal(
            ProviderObservation.Ambiguous,
            mock.GetLastOutcome(id));
    }

    [Fact]
    public void ProviderAcceptance_ShouldNotTriggerResend()
    {
        var mock = new MockDeliveryCoordinator();
        var scope = Scope(NotificationModule.Learning);

        var id = mock.PersistOnce(Guid.NewGuid(), scope);

        Assert.True(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Accepted));

        Assert.False(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Accepted));

        Assert.Equal(1, mock.GetAttemptCount(id));

        // Provider acceptance does NOT prove device delivery.
    }

    [Fact]
    public void OtherModuleOrProfile_ShouldNotRecordAttempt()
    {
        var mock = new MockDeliveryCoordinator();

        var owner = Scope(NotificationModule.Learning);
        var otherModule = Scope(NotificationModule.Hobbies);
        var otherProfile = Scope(NotificationModule.Learning);

        var id = mock.PersistOnce(Guid.NewGuid(), owner);

        Assert.False(mock.RecordDeliveryAttempt(
            id, otherModule, ProviderObservation.Accepted));

        Assert.False(mock.RecordDeliveryAttempt(
            id, otherProfile, ProviderObservation.Accepted));

        Assert.Equal(0, mock.GetAttemptCount(id));
    }

    [Fact]
    public void PermanentFailure_ShouldBlockFurtherAttempts()
    {
        var mock = new MockDeliveryCoordinator();
        var scope = Scope(NotificationModule.Social);

        var id = mock.PersistOnce(Guid.NewGuid(), scope);

        Assert.True(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.PermanentFailure));

        Assert.False(mock.RecordDeliveryAttempt(
            id, scope, ProviderObservation.Accepted));

        Assert.Equal(1, mock.GetAttemptCount(id));
    }

    [Fact]
    public void SameEventDifferentProfiles_ShouldRemainIndependent()
    {
        var mock = new MockDeliveryCoordinator();

        var eventId = Guid.NewGuid();
        var firstScope = Scope(NotificationModule.Learning);
        var secondScope = Scope(NotificationModule.Learning);

        var firstId = mock.PersistOnce(eventId, firstScope);
        var secondId = mock.PersistOnce(eventId, secondScope);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, mock.NotificationCount);
    }
}
