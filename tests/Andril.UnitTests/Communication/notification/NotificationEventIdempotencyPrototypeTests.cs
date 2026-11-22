
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D10-B: TEST-ONLY notification event idempotency prototype.
///
/// Simulates an atomic notification write and event deduplication.
///
/// This is NOT a production event processor or database repository.
/// No real authorization, database, provider or network access.
/// </summary>
public sealed class NotificationEventIdempotencyPrototypeTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private enum MockOutcome
    {
        Persisted,
        Duplicate,
        Denied,
        RetryableFailure
    }

    private sealed record MockResult(
        MockOutcome Outcome,
        Guid? NotificationId);

    // Test-only simulation of a lost success acknowledgement.
    private sealed class MockAcknowledgementLostException : Exception
    {
    }

    // PROPOSED TEST KEY ONLY.
    // Production idempotency key remains pending M2/M6 review.
    private readonly record struct EventScopeKey(
        Guid DomainEventId,
        NotificationModule Module,
        Guid RecipientModuleProfileId);

    private sealed class MockNotificationEventProcessor
    {
        private readonly object _gate = new();

        private readonly Dictionary<EventScopeKey, NotificationEntry>
            _stored = new();

        public IReadOnlyList<NotificationEntry> Snapshot()
        {
            lock (_gate)
            {
                return _stored.Values.ToArray();
            }
        }

        public Task<MockResult> ProcessAsync(
            Guid domainEventId,
            NotificationModuleScope? recipientScope,
            bool simulateWriteFailure = false,
            bool simulateAcknowledgementLoss = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // This prototype receives a simulated scope.
            // It does NOT establish real account authorization.
            if (domainEventId == Guid.Empty ||
                recipientScope is null ||
                recipientScope.RecipientModuleProfileId == Guid.Empty ||
                !Enum.IsDefined(recipientScope.Module))
            {
                return Task.FromResult(
                    new MockResult(MockOutcome.Denied, null));
            }

            var key = new EventScopeKey(
                domainEventId,
                recipientScope.Module,
                recipientScope.RecipientModuleProfileId);

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Simulate an already committed event.
                if (_stored.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(
                        new MockResult(
                            MockOutcome.Duplicate,
                            existing.Id));
                }

                // Simulate a failure before committing.
                // No key or notification is stored.
                if (simulateWriteFailure)
                {
                    return Task.FromResult(
                        new MockResult(
                            MockOutcome.RetryableFailure,
                            null));
                }

                // Generic mock content only.
                // No private identity or message preview.
                var notification = new NotificationEntry(
                    Guid.NewGuid(),
                    recipientScope,
                    "mock_event",
                    "New activity",
                    null,
                    CreatedAt);

                // Simulated atomic commit.
                _stored.Add(key, notification);

                // Simulate a committed write whose success response
                // never reaches the caller. A retry must deduplicate.
                if (simulateAcknowledgementLoss)
                {
                    throw new MockAcknowledgementLostException();
                }

                return Task.FromResult(
                    new MockResult(
                        MockOutcome.Persisted,
                        notification.Id));
            }
        }
    }

    private static NotificationModuleScope CreateScope(
        NotificationModule module,
        Guid? profileId = null)
    {
        return new NotificationModuleScope(
            module,
            profileId ?? Guid.NewGuid());
    }

    [Fact]
    public async Task RepeatedEvent_ShouldCreateOnlyOneNotification()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();

        var scope = CreateScope(NotificationModule.Learning);

        var first = await processor.ProcessAsync(eventId, scope);
        var second = await processor.ProcessAsync(eventId, scope);

        Assert.Equal(MockOutcome.Persisted, first.Outcome);
        Assert.Equal(MockOutcome.Duplicate, second.Outcome);

        Assert.Equal(first.NotificationId, second.NotificationId);
        Assert.Single(processor.Snapshot());
    }

    [Fact]
    public async Task DifferentEvents_ShouldRemainIndependent()
    {
        var processor = new MockNotificationEventProcessor();
        var scope = CreateScope(NotificationModule.Hobbies);

        var first = await processor.ProcessAsync(
            Guid.NewGuid(),
            scope);

        var second = await processor.ProcessAsync(
            Guid.NewGuid(),
            scope);

        Assert.Equal(MockOutcome.Persisted, first.Outcome);
        Assert.Equal(MockOutcome.Persisted, second.Outcome);
        Assert.NotEqual(first.NotificationId, second.NotificationId);

        Assert.Equal(2, processor.Snapshot().Count);
    }

    [Fact]
    public async Task SameEvent_ShouldKeepModuleProfilesIndependent()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var learning = CreateScope(
            NotificationModule.Learning,
            profileId);

        var hobbies = CreateScope(
            NotificationModule.Hobbies,
            profileId);

        var social = CreateScope(
            NotificationModule.Social);

        var results = await Task.WhenAll(
            processor.ProcessAsync(eventId, learning),
            processor.ProcessAsync(eventId, hobbies),
            processor.ProcessAsync(eventId, social));

        Assert.All(
            results,
            result =>
                Assert.Equal(
                    MockOutcome.Persisted,
                    result.Outcome));

        Assert.Equal(3, processor.Snapshot().Count);

        Assert.Equal(
            3,
            processor.Snapshot()
                .Select(entry => entry.Id)
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task FailedWrite_ShouldPermitSafeRetry()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();

        var scope = CreateScope(NotificationModule.Learning);

        var failed = await processor.ProcessAsync(
            eventId,
            scope,
            simulateWriteFailure: true);

        Assert.Equal(
            MockOutcome.RetryableFailure,
            failed.Outcome);

        Assert.Empty(processor.Snapshot());

        var retry = await processor.ProcessAsync(eventId, scope);

        Assert.Equal(MockOutcome.Persisted, retry.Outcome);
        Assert.Single(processor.Snapshot());

        var duplicate = await processor.ProcessAsync(
            eventId,
            scope);

        Assert.Equal(MockOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(retry.NotificationId, duplicate.NotificationId);
    }

    [Fact]
    public async Task ConcurrentRetries_ShouldPersistOneNotification()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();

        var scope = CreateScope(NotificationModule.Social);

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(
                () => processor.ProcessAsync(eventId, scope)))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(
            1,
            results.Count(
                result =>
                    result.Outcome == MockOutcome.Persisted));

        Assert.Equal(
            31,
            results.Count(
                result =>
                    result.Outcome == MockOutcome.Duplicate));

        Assert.Single(processor.Snapshot());

        Assert.Single(
            results
                .Select(result => result.NotificationId)
                .Distinct());
    }

    [Fact]
    public async Task InvalidEventOrScope_ShouldFailClosed()
    {
        var processor = new MockNotificationEventProcessor();
        var scope = CreateScope(NotificationModule.Hobbies);

        var invalidEvent = await processor.ProcessAsync(
            Guid.Empty,
            scope);

        var missingScope = await processor.ProcessAsync(
            Guid.NewGuid(),
            null);

        Assert.Equal(MockOutcome.Denied, invalidEvent.Outcome);
        Assert.Equal(MockOutcome.Denied, missingScope.Outcome);

        Assert.Empty(processor.Snapshot());
    }

    [Fact]
    public async Task Cancellation_ShouldPreventProcessing()
    {
        var processor = new MockNotificationEventProcessor();
        var scope = CreateScope(NotificationModule.Learning);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await processor.ProcessAsync(
                    Guid.NewGuid(),
                    scope,
                    cancellationToken: cancellation.Token));

        Assert.Empty(processor.Snapshot());
    }

    [Fact]
    public async Task MockNotifications_ShouldNotContainPrivatePreview()
    {
        var processor = new MockNotificationEventProcessor();

        await processor.ProcessAsync(
            Guid.NewGuid(),
            CreateScope(NotificationModule.Social));

        var notification = Assert.Single(
            processor.Snapshot());

        Assert.Equal("New activity", notification.Title);
        Assert.Null(notification.Preview);
    }

    [Fact]
    public async Task SameEventAndModule_DifferentProfiles_ShouldRemainIndependent()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();

        var firstProfile = CreateScope(
            NotificationModule.Learning);

        var secondProfile = CreateScope(
            NotificationModule.Learning);

        var first = await processor.ProcessAsync(
            eventId,
            firstProfile);

        var second = await processor.ProcessAsync(
            eventId,
            secondProfile);

        var repeatedFirst = await processor.ProcessAsync(
            eventId,
            firstProfile);

        Assert.Equal(MockOutcome.Persisted, first.Outcome);
        Assert.Equal(MockOutcome.Persisted, second.Outcome);
        Assert.Equal(MockOutcome.Duplicate, repeatedFirst.Outcome);

        Assert.NotEqual(first.NotificationId, second.NotificationId);
        Assert.Equal(first.NotificationId, repeatedFirst.NotificationId);

        var notifications = processor.Snapshot();

        Assert.Equal(2, notifications.Count);

        Assert.Contains(
            notifications,
            entry => entry.RecipientScope == firstProfile);

        Assert.Contains(
            notifications,
            entry => entry.RecipientScope == secondProfile);
    }

    [Fact]
    public async Task LostAcknowledgement_ShouldNotDuplicateOnRetry()
    {
        var processor = new MockNotificationEventProcessor();
        var eventId = Guid.NewGuid();
        var scope = CreateScope(NotificationModule.Learning);

        // Persist successfully, then simulate losing the acknowledgement.
        await Assert.ThrowsAsync<MockAcknowledgementLostException>(
            async () =>
                await processor.ProcessAsync(
                    eventId,
                    scope,
                    simulateAcknowledgementLoss: true));

        var committed = Assert.Single(processor.Snapshot());

        // A repeat delivery must return the original record ID.
        var retry = await processor.ProcessAsync(eventId, scope);

        Assert.Equal(MockOutcome.Duplicate, retry.Outcome);
        Assert.Equal(committed.Id, retry.NotificationId);
        Assert.Single(processor.Snapshot());
    }

}
