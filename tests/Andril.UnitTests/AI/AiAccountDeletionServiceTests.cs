
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic orchestration tests for AI-PRIV-01.
/// No real worker authorization, database, or physical data deletion.
/// </summary>
public sealed class AiAccountDeletionServiceTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();

    [Fact]
    public void Constructor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiAccountDeletionService(null!));
    }

    [Theory]
    [InlineData(true, false, "accountId")]
    [InlineData(false, true, "deletionEventId")]
    [InlineData(true, true, "accountId")]
    public async Task EmptyIdentifiers_AreRejectedBeforeRepositoryCall(
        bool emptyAccount, bool emptyEvent, string expectedParameter)
    {
        var repo = new FakeRepository();
        var service = new AiAccountDeletionService(repo);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnsureCleanupScheduledAsync(
                emptyAccount ? Guid.Empty : AccountId,
                emptyEvent ? Guid.Empty : EventId));

        Assert.Equal(expectedParameter, error.ParamName);
        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiAccountDeletionScheduleOutcome.Scheduled)]
    [InlineData(AiAccountDeletionScheduleOutcome.AlreadyRecorded)]
    public async Task ValidOutcome_IsReturnedWithExactIdentifiersAndToken(
        AiAccountDeletionScheduleOutcome outcome)
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, _, _) => Task.FromResult(outcome)
        };

        var service = new AiAccountDeletionService(repo);

        var result = await service.EnsureCleanupScheduledAsync(
            AccountId, EventId, cts.Token);

        Assert.Equal(outcome, result);

        Assert.Equal(
            (AccountId, EventId, cts.Token),
            Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task RepeatedEvent_AcceptsAlreadyRecordedOutcome()
    {
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _, _) => Task.FromResult(++attempts == 1
                ? AiAccountDeletionScheduleOutcome.Scheduled
                : AiAccountDeletionScheduleOutcome.AlreadyRecorded)
        };

        var service = new AiAccountDeletionService(repo);

        Assert.Equal(
            AiAccountDeletionScheduleOutcome.Scheduled,
            await service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Equal(
            AiAccountDeletionScheduleOutcome.AlreadyRecorded,
            await service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal(
                (AccountId, EventId, CancellationToken.None), call));
    }

    [Fact]
    public async Task UnconfirmedOrMissingEvent_IsDenied()
    {
        var repo = new FakeRepository
        {
            Handler = (_, _, _) => Task.FromResult(
                AiAccountDeletionScheduleOutcome.NotConfirmedOrMissing)
        };

        var service = new AiAccountDeletionService(repo);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Single(repo.Calls);
    }

    [Theory]
    [InlineData(AiAccountDeletionScheduleOutcome.Unknown)]
    [InlineData((AiAccountDeletionScheduleOutcome)(-1))]
    [InlineData((AiAccountDeletionScheduleOutcome)99)]
    public async Task UnknownOutcome_FailsClosed(
        AiAccountDeletionScheduleOutcome outcome)
    {
        var repo = new FakeRepository
        {
            Handler = (_, _, _) => Task.FromResult(outcome)
        };

        var service = new AiAccountDeletionService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task RepositoryAuthorizationFailure_Propagates()
    {
        var failure = new UnauthorizedAccessException(
            "Synthetic denial");

        var repo = new FakeRepository
        {
            Handler = (_, _, _) =>
                Task.FromException<AiAccountDeletionScheduleOutcome>(
                    failure)
        };

        var service = new AiAccountDeletionService(repo);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Same(failure, error);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task StorageFailure_IsNotReportedAsSuccess()
    {
        var failure = new InvalidOperationException(
            "Synthetic storage failure");

        var repo = new FakeRepository
        {
            Handler = (_, _, _) =>
                Task.FromException<AiAccountDeletionScheduleOutcome>(
                    failure)
        };

        var service = new AiAccountDeletionService(repo);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Same(failure, error);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotCallRepository()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var repo = new FakeRepository();
        var service = new AiAccountDeletionService(repo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureCleanupScheduledAsync(
                AccountId, EventId, cts.Token));

        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiAccountDeletionScheduleOutcome.Scheduled)]
    [InlineData(AiAccountDeletionScheduleOutcome.AlreadyRecorded)]
    public async Task CancellationDuringScheduling_DoesNotReportSuccess(
        AiAccountDeletionScheduleOutcome outcome)
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, _, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();

                return Task.FromResult(outcome);
            }
        };

        var service = new AiAccountDeletionService(repo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureCleanupScheduledAsync(
                AccountId, EventId, cts.Token));

        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task RepositoryCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, _, token) =>
            {
                cts.Cancel();

                return Task.FromCanceled<AiAccountDeletionScheduleOutcome>(
                    token);
            }
        };

        var service = new AiAccountDeletionService(repo);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnsureCleanupScheduledAsync(
                AccountId, EventId, cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task ExplicitRetryAfterUncertainFailure_PreservesIdentifiers()
    {
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _, _) => ++attempts == 1
                ? Task.FromException<AiAccountDeletionScheduleOutcome>(
                    new InvalidOperationException(
                        "Synthetic uncertain outcome"))
                : Task.FromResult(
                    AiAccountDeletionScheduleOutcome.AlreadyRecorded)
        };

        var service = new AiAccountDeletionService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Single(repo.Calls);

        Assert.Equal(
            AiAccountDeletionScheduleOutcome.AlreadyRecorded,
            await service.EnsureCleanupScheduledAsync(AccountId, EventId));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal(
                (AccountId, EventId, CancellationToken.None), call));
    }

    // Synthetic test repository — defaults to denial.

    private sealed class FakeRepository : IAiAccountDeletionRepository
    {
        public List<(Guid AccountId, Guid EventId, CancellationToken Token)>
            Calls
        { get; } = new();

        public Func<Guid, Guid, CancellationToken,
            Task<AiAccountDeletionScheduleOutcome>> Handler
        { get; init; }
            = (_, _, _) => Task.FromResult(
                AiAccountDeletionScheduleOutcome.NotConfirmedOrMissing);

        public Task<AiAccountDeletionScheduleOutcome> EnsureCleanupScheduledAsync(
            Guid accountId,
            Guid deletionEventId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((accountId, deletionEventId, cancellationToken));

            return Handler(accountId, deletionEventId, cancellationToken);
        }
    }
}
