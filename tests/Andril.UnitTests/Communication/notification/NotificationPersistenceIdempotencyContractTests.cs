using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// ANDRIL M4 D12-B3: TEST-ONLY deterministic idempotency contract prototype.
///
/// No PostgreSQL, migration, DbContext, production DI, push provider, or network.
/// This does NOT prove database transactions, RLS, or multi-process safety.
///
/// Producer namespace and notification kind are PROPOSED key dimensions,
/// NOT approved production schema decisions. Payload fingerprint values
/// are inert dummy identifiers, not private message content.
/// </summary>
public sealed class NotificationPersistenceIdempotencyContractTests
{
    private enum MockPersistenceOutcome
    {
        Created,
        Existing,
        Conflict,
        Denied
    }

    private enum SimulatedFailurePoint
    {
        None,
        AfterReceiptStaged,
        AfterCommitBeforeAcknowledgement
    }

    private sealed class SimulatedWriteFailureException : Exception
    {
    }

    private sealed class SimulatedAcknowledgementLossException : Exception
    {
    }

    private readonly record struct ProposedEventKey(
        string ProducerNamespace,
        Guid DomainEventId,
        NotificationModule Module,
        Guid RecipientModuleProfileId,
        string NotificationKind);

    private sealed record SimulatedEvent(
        ProposedEventKey Key,
        string PayloadFingerprint);

    private sealed record MockPersistenceResult(
        MockPersistenceOutcome Outcome,
        Guid? NotificationId);

    private sealed record MockReceipt(
        Guid NotificationId,
        string PayloadFingerprint);

    private sealed record MockNotification(
        Guid Id,
        NotificationModule Module,
        Guid RecipientModuleProfileId);

    /// <summary>
    /// Simulates a transactional receipt + notification write.
    /// Dictionary snapshots are staged then swapped under one lock.
    /// The shared in-memory lock is NOT a substitute for PostgreSQL uniqueness.
    /// </summary>
    private sealed class MockAtomicPersistenceStore
    {
        private readonly object _gate = new();
        private Dictionary<ProposedEventKey, MockReceipt> _receipts = new();
        private Dictionary<Guid, MockNotification> _notifications = new();

        public (int Receipts, int Notifications) Counts
        {
            get
            {
                lock (_gate)
                {
                    return (_receipts.Count, _notifications.Count);
                }
            }
        }

        public Guid SingleNotificationId
        {
            get
            {
                lock (_gate)
                {
                    return Assert.Single(_notifications.Keys);
                }
            }
        }

        public string? FingerprintFor(ProposedEventKey key)
        {
            lock (_gate)
            {
                return _receipts.TryGetValue(key, out var receipt)
                    ? receipt.PayloadFingerprint
                    : null;
            }
        }

        public MockPersistenceResult Process(
            SimulatedEvent input,
            SimulatedFailurePoint failure = SimulatedFailurePoint.None)
        {
            var key = input.Key;

            // This checks only mock input shape. It is NOT M2 authorization.
            // Event identity and scope must be proven by trusted backend systems.
            if (string.IsNullOrWhiteSpace(key.ProducerNamespace) ||
                key.DomainEventId == Guid.Empty ||
                !Enum.IsDefined(key.Module) ||
                key.RecipientModuleProfileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(key.NotificationKind) ||
                string.IsNullOrWhiteSpace(input.PayloadFingerprint))
            {
                return new MockPersistenceResult(
                    MockPersistenceOutcome.Denied, null);
            }

            lock (_gate)
            {
                if (_receipts.TryGetValue(key, out var existing))
                {
                    if (!string.Equals(
                        existing.PayloadFingerprint,
                        input.PayloadFingerprint,
                        StringComparison.Ordinal))
                    {
                        // Do not return the existing ID to a conflicting request.
                        return new MockPersistenceResult(
                            MockPersistenceOutcome.Conflict, null);
                    }

                    if (!_notifications.ContainsKey(existing.NotificationId))
                    {
                        // Simulated fail-closed check for inconsistent state.
                        return new MockPersistenceResult(
                            MockPersistenceOutcome.Denied, null);
                    }

                    return new MockPersistenceResult(
                        MockPersistenceOutcome.Existing,
                        existing.NotificationId);
                }

                // Stage both changes without modifying committed mock state.
                var newId = Guid.NewGuid();
                var stagedReceipts = new Dictionary<ProposedEventKey, MockReceipt>(
                    _receipts);
                var stagedNotifications = new Dictionary<Guid, MockNotification>(
                    _notifications);

                stagedReceipts.Add(
                    key,
                    new MockReceipt(newId, input.PayloadFingerprint));

                if (failure == SimulatedFailurePoint.AfterReceiptStaged)
                {
                    throw new SimulatedWriteFailureException();
                }

                stagedNotifications.Add(
                    newId,
                    new MockNotification(
                        newId,
                        key.Module,
                        key.RecipientModuleProfileId));

                // The mock's atomic commit boundary (under _gate).
                _receipts = stagedReceipts;
                _notifications = stagedNotifications;

                if (failure ==
                    SimulatedFailurePoint.AfterCommitBeforeAcknowledgement)
                {
                    throw new SimulatedAcknowledgementLossException();
                }

                return new MockPersistenceResult(
                    MockPersistenceOutcome.Created, newId);
            }
        }
    }

    private static SimulatedEvent Event(
        Guid? domainEventId = null,
        NotificationModule module = NotificationModule.Learning,
        Guid? profileId = null,
        string fingerprint = "sha256:dummy-payload-v1")
    {
        var key = new ProposedEventKey(
            "trusted-mock-producer",
            domainEventId ?? Guid.NewGuid(),
            module,
            profileId ?? Guid.NewGuid(),
            "mock-activity");

        return new SimulatedEvent(key, fingerprint);
    }

    [Fact]
    public void SameKeyAndPayload_ShouldReconcileToOriginalNotification()
    {
        var store = new MockAtomicPersistenceStore();
        var input = Event();

        var first = store.Process(input);
        var repeated = store.Process(input);

        Assert.Equal(MockPersistenceOutcome.Created, first.Outcome);
        Assert.Equal(MockPersistenceOutcome.Existing, repeated.Outcome);
        Assert.NotNull(first.NotificationId);
        Assert.Equal(first.NotificationId, repeated.NotificationId);
        Assert.Equal((1, 1), store.Counts);
    }

    [Fact]
    public void ConflictingPayload_ShouldFailClosedWithoutOverwrite()
    {
        var store = new MockAtomicPersistenceStore();
        var original = Event();
        var changed = original with
        {
            PayloadFingerprint = "sha256:dummy-conflicting-payload"
        };

        var first = store.Process(original);
        var conflict = store.Process(changed);

        Assert.Equal(MockPersistenceOutcome.Created, first.Outcome);
        Assert.Equal(MockPersistenceOutcome.Conflict, conflict.Outcome);
        Assert.Null(conflict.NotificationId);
        Assert.Equal((1, 1), store.Counts);
        Assert.Equal(
            original.PayloadFingerprint,
            store.FingerprintFor(original.Key));
        Assert.Equal(first.NotificationId, store.SingleNotificationId);
    }

    [Fact]
    public async Task ConcurrentDuplicateWriters_ShouldCreateOnlyOneRecordPair()
    {
        var store = new MockAtomicPersistenceStore();
        var input = Event();

        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);

        Task<MockPersistenceResult> Launch() => Task.Run(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Simulated writers did not start.");
            }
            return store.Process(input);
        });

        var firstTask = Launch();
        var secondTask = Launch();
        try
        {
            Assert.True(
                ready.Wait(TimeSpan.FromSeconds(10)),
                "Both simulated writers must be ready.");
        }
        finally
        {
            start.Set();
        }

        var outcomes = await Task.WhenAll(firstTask, secondTask);



        var created = Assert.Single(
            outcomes,
            result => result.Outcome == MockPersistenceOutcome.Created);

        var existing = Assert.Single(
            outcomes,
            result => result.Outcome == MockPersistenceOutcome.Existing);

        // Both writers must refer to the same notification.
        Assert.NotNull(created.NotificationId);
        Assert.Equal(
            created.NotificationId,
            existing.NotificationId);

        // One idempotency receipt and one notification.
        Assert.Equal((1, 1), store.Counts);

        Assert.Equal(
            created.NotificationId,
            store.SingleNotificationId);

    }

    [Fact]
    public void FailureBeforeCommit_ShouldLeaveNoPartialRecords()
    {
        var store = new MockAtomicPersistenceStore();
        var input = Event();

        Assert.Throws<SimulatedWriteFailureException>(() =>
            store.Process(input, SimulatedFailurePoint.AfterReceiptStaged));

        Assert.Equal((0, 0), store.Counts);
        Assert.Null(store.FingerprintFor(input.Key));

        var retry = store.Process(input);
        Assert.Equal(MockPersistenceOutcome.Created, retry.Outcome);
        Assert.Equal((1, 1), store.Counts);
    }

    [Fact]
    public void LostCommitAcknowledgement_ShouldReconcileToCommittedId()
    {
        var store = new MockAtomicPersistenceStore();
        var input = Event();

        Assert.Throws<SimulatedAcknowledgementLossException>(() =>
            store.Process(
                input,
                SimulatedFailurePoint.AfterCommitBeforeAcknowledgement));

        Assert.Equal((1, 1), store.Counts);
        var committedId = store.SingleNotificationId;

        var retry = store.Process(input);
        Assert.Equal(MockPersistenceOutcome.Existing, retry.Outcome);
        Assert.Equal(committedId, retry.NotificationId);
        Assert.Equal((1, 1), store.Counts);
    }

    [Fact]
    public void SameEventDifferentPrivateProfiles_ShouldBeIndependent()
    {
        var store = new MockAtomicPersistenceStore();
        var eventId = Guid.NewGuid();
        var firstScope = Event(domainEventId: eventId);
        var secondScope = Event(
            domainEventId: eventId,
            profileId: Guid.NewGuid());
        var thirdModule = Event(
            domainEventId: eventId,
            module: NotificationModule.Hobbies,
            profileId: firstScope.Key.RecipientModuleProfileId);

        // Each different (module, private profile) scope is independent.
        var first = store.Process(firstScope);
        var second = store.Process(secondScope);
        var third = store.Process(thirdModule);

        Assert.Equal(MockPersistenceOutcome.Created, first.Outcome);
        Assert.Equal(MockPersistenceOutcome.Created, second.Outcome);
        Assert.Equal(MockPersistenceOutcome.Created, third.Outcome);
        Assert.Equal(3, new[]
        {
            first.NotificationId,
            second.NotificationId,
            third.NotificationId
        }.Distinct().Count());
        Assert.Equal((3, 3), store.Counts);
    }
}
