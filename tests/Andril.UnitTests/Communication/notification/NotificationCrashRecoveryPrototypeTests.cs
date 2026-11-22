using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D11-C: TEST-ONLY conflicting-payload and simulated restart tests.
/// A shared in-memory state imitates durable storage across coordinator
/// instances; it does NOT prove persistence across a real process restart.
/// No PostgreSQL, provider, network, production DI, or authorization.
/// The key structure and retry behavior remain PROPOSED.
/// </summary>
public sealed class NotificationCrashRecoveryPrototypeTests
{
    private enum PersistOutcome
    {
        Created,
        Duplicate,
        Conflict,
        RetryableFailure,
        Denied
    }

    private enum MockDeliveryState
    {
        Pending,
        Unknown
    }

    private sealed record PersistResult(PersistOutcome Outcome, Guid? NotificationId);

    // Test-only key, NOT an approved database uniqueness definition.
    private readonly record struct MockKey(
        Guid EventId,
        NotificationModule Module,
        Guid RecipientModuleProfileId);

    private sealed class MockReceipt
    {
        internal MockReceipt(Guid id, NotificationModuleScope scope, byte[] fingerprint)
        {
            Id = id;
            Scope = scope;
            Fingerprint = fingerprint;
        }

        internal Guid Id { get; }
        internal NotificationModuleScope Scope { get; }
        internal byte[] Fingerprint { get; }
        internal MockDeliveryState DeliveryState { get; set; } = MockDeliveryState.Pending;
        internal int DeliveryAttempts { get; set; }
    }

    private sealed class MockCommittedState
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<MockKey, MockReceipt> Receipts = new();
    }

    private sealed class MockProcessor
    {
        private readonly MockCommittedState _state;

        internal MockProcessor(MockCommittedState state) => _state = state;

        internal PersistResult Persist(
            Guid eventId,
            NotificationModuleScope? scope,
            string? syntheticPayload,
            bool failBeforeCommit = false)
        {
            // Synthetic inputs only; real callers must use M2 authorization.
            if (eventId == Guid.Empty || scope is null ||
                scope.RecipientModuleProfileId == Guid.Empty ||
                !Enum.IsDefined(scope.Module) ||
                string.IsNullOrWhiteSpace(syntheticPayload))
            {
                return new PersistResult(PersistOutcome.Denied, null);
            }

            // Hash is a mock payload identity only. Canonicalization and
            // production fingerprint format remain pending review.
            var fingerprint = SHA256.HashData(
                Encoding.UTF8.GetBytes(syntheticPayload));
            var key = new MockKey(
                eventId, scope.Module, scope.RecipientModuleProfileId);

            lock (_state.Gate)
            {
                if (_state.Receipts.TryGetValue(key, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                        existing.Fingerprint, fingerprint))
                    {
                        // Never reveal an existing ID for a conflicting event.
                        return new PersistResult(PersistOutcome.Conflict, null);
                    }

                    return new PersistResult(PersistOutcome.Duplicate, existing.Id);
                }

                if (failBeforeCommit)
                {
                    return new PersistResult(PersistOutcome.RetryableFailure, null);
                }

                var id = Guid.NewGuid();
                _state.Receipts.Add(key, new MockReceipt(id, scope, fingerprint));
                return new PersistResult(PersistOutcome.Created, id);
            }
        }

        internal bool RecordUnknownProviderOutcome(
            Guid notificationId,
            NotificationModuleScope scope)
        {
            lock (_state.Gate)
            {
                var receipt = _state.Receipts.Values.SingleOrDefault(
                    item => item.Id == notificationId);

                if (receipt is null || receipt.Scope != scope ||
                    receipt.DeliveryState != MockDeliveryState.Pending)
                {
                    return false;
                }

                receipt.DeliveryAttempts++;
                receipt.DeliveryState = MockDeliveryState.Unknown;
                return true;
            }
        }

        internal int NotificationCount
        {
            get { lock (_state.Gate) return _state.Receipts.Count; }
        }

        internal int AttemptsFor(Guid notificationId)
        {
            lock (_state.Gate)
            {
                return _state.Receipts.Values
                    .Single(item => item.Id == notificationId)
                    .DeliveryAttempts;
            }
        }
    }

    private static NotificationModuleScope Scope(
        NotificationModule module = NotificationModule.Learning) =>
        new(module, Guid.NewGuid());

    [Fact]
    public void SameEventAndPayload_ShouldReconcileToExistingId()
    {
        var processor = new MockProcessor(new MockCommittedState());
        var scope = Scope();
        var eventId = Guid.NewGuid();

        var first = processor.Persist(eventId, scope, "safe-template-v1");
        var repeated = processor.Persist(eventId, scope, "safe-template-v1");

        Assert.Equal(PersistOutcome.Created, first.Outcome);
        Assert.Equal(PersistOutcome.Duplicate, repeated.Outcome);
        Assert.Equal(first.NotificationId, repeated.NotificationId);
        Assert.Equal(1, processor.NotificationCount);
    }

    [Fact]
    public void SameEventDifferentPayload_ShouldFailClosedWithoutOverwrite()
    {
        var processor = new MockProcessor(new MockCommittedState());
        var scope = Scope();
        var eventId = Guid.NewGuid();

        var original = processor.Persist(eventId, scope, "safe-template-v1");
        var conflict = processor.Persist(eventId, scope, "different-template-v2");
        var repeatOriginal = processor.Persist(eventId, scope, "safe-template-v1");

        Assert.Equal(PersistOutcome.Conflict, conflict.Outcome);
        Assert.Null(conflict.NotificationId);
        Assert.Equal(PersistOutcome.Duplicate, repeatOriginal.Outcome);
        Assert.Equal(original.NotificationId, repeatOriginal.NotificationId);
        Assert.Equal(1, processor.NotificationCount);
    }

    [Fact]
    public void SimulatedRestartAfterCommit_ShouldReconcileExistingId()
    {
        var committedState = new MockCommittedState();
        var scope = Scope(NotificationModule.Hobbies);
        var eventId = Guid.NewGuid();

        var beforeRestart = new MockProcessor(committedState);
        var created = beforeRestart.Persist(eventId, scope, "safe-template-v1");

        // Simulated restart: a NEW coordinator object reads the SAME
        // mock committed state. No disk write or real process restart.
        var afterRestart = new MockProcessor(committedState);
        var retry = afterRestart.Persist(eventId, scope, "safe-template-v1");

        Assert.Equal(PersistOutcome.Created, created.Outcome);
        Assert.Equal(PersistOutcome.Duplicate, retry.Outcome);
        Assert.Equal(created.NotificationId, retry.NotificationId);
        Assert.Equal(1, afterRestart.NotificationCount);
    }

    [Fact]
    public void FailedWriteBeforeCommit_ShouldAllowRetry()
    {
        var processor = new MockProcessor(new MockCommittedState());
        var scope = Scope();
        var eventId = Guid.NewGuid();

        var failure = processor.Persist(
            eventId, scope, "safe-template-v1", failBeforeCommit: true);
        Assert.Equal(PersistOutcome.RetryableFailure, failure.Outcome);
        Assert.Equal(0, processor.NotificationCount);

        var retry = processor.Persist(eventId, scope, "safe-template-v1");
        Assert.Equal(PersistOutcome.Created, retry.Outcome);
        Assert.Equal(1, processor.NotificationCount);
    }

    [Fact]
    public void UnknownDeliveryAfterSimulatedRestart_ShouldNotBlindlyResend()
    {
        var committedState = new MockCommittedState();
        var scope = Scope(NotificationModule.Social);
        var beforeRestart = new MockProcessor(committedState);
        var created = beforeRestart.Persist(
            Guid.NewGuid(), scope, "safe-template-v1");
        var id = Assert.IsType<Guid>(created.NotificationId);

        Assert.True(beforeRestart.RecordUnknownProviderOutcome(id, scope));

        var afterRestart = new MockProcessor(committedState);
        Assert.False(afterRestart.RecordUnknownProviderOutcome(id, scope));
        Assert.Equal(1, afterRestart.AttemptsFor(id));
        Assert.Equal(1, afterRestart.NotificationCount);
    }

    [Fact]
    public void WrongProfileOrModule_ShouldNotModifyDeliveryState()
    {
        var processor = new MockProcessor(new MockCommittedState());
        var owner = Scope(NotificationModule.Learning);
        var created = processor.Persist(
            Guid.NewGuid(), owner, "safe-template-v1");
        var id = Assert.IsType<Guid>(created.NotificationId);

        Assert.False(processor.RecordUnknownProviderOutcome(
            id, Scope(NotificationModule.Learning)));
        Assert.False(processor.RecordUnknownProviderOutcome(
            id, Scope(NotificationModule.Hobbies)));
        Assert.Equal(0, processor.AttemptsFor(id));
    }
}
