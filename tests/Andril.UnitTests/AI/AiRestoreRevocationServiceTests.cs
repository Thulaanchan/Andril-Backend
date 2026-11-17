
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic recovery orchestration tests, not database restore tests.
/// </summary>
public sealed class AiRestoreRevocationServiceTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    [Fact]
    public void Constructor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiRestoreRevocationService(null!));
    }

    [Fact]
    public async Task EmptyRunId_IsRejectedBeforeRepositoryCall()
    {
        var repo = new FakeRepository();
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnsureRevocationsReconciledAsync(Guid.Empty));

        Assert.Equal("recoveryRunId", error.ParamName);
        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiRestoreRevocationOutcome.Reconciled)]
    [InlineData(AiRestoreRevocationOutcome.AlreadyReconciled)]
    public async Task AcceptedOutcome_PreservesRunIdAndToken(
        AiRestoreRevocationOutcome outcome)
    {
        using var cts = new CancellationTokenSource();
        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(outcome)
        };
        var service = new AiRestoreRevocationService(repo);

        Assert.Equal(outcome, await service
            .EnsureRevocationsReconciledAsync(RunId, cts.Token));

        Assert.Equal((RunId, cts.Token), Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task NotReady_RejectsReconciliation()
    {
        var repo = new FakeRepository();
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal("AI recovery reconciliation is not ready.",
            error.Message);

        Assert.Single(repo.Calls);
    }

    [Theory]
    [InlineData(AiRestoreRevocationOutcome.Unknown)]
    [InlineData((AiRestoreRevocationOutcome)(-1))]
    [InlineData((AiRestoreRevocationOutcome)99)]
    public async Task UnknownOutcome_FailsClosed(
        AiRestoreRevocationOutcome outcome)
    {
        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(outcome)
        };
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal(
            "AI recovery reconciliation could not be verified.",
            error.Message);

        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task RepositoryAuthorizationFailure_Propagates()
    {
        var failure = new UnauthorizedAccessException("Synthetic denial");

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromException<AiRestoreRevocationOutcome>(
                failure)
        };
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

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
            Handler = (_, _) => Task.FromException<AiRestoreRevocationOutcome>(
                failure)
        };
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Same(failure, error);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotCallRepository()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var repo = new FakeRepository();
        var service = new AiRestoreRevocationService(repo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId, cts.Token));

        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiRestoreRevocationOutcome.Reconciled)]
    [InlineData(AiRestoreRevocationOutcome.AlreadyReconciled)]
    public async Task CancellationDuringReconciliation_DoesNotReportSuccess(
        AiRestoreRevocationOutcome outcome)
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();

                return Task.FromResult(outcome);
            }
        };
        var service = new AiRestoreRevocationService(repo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId, cts.Token));

        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task RepositoryCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, token) =>
            {
                cts.Cancel();

                return Task.FromCanceled<AiRestoreRevocationOutcome>(token);
            }
        };
        var service = new AiRestoreRevocationService(repo);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId, cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task ExplicitRetryAfterFailure_PreservesRunId()
    {
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => ++attempts == 1
                ? Task.FromException<AiRestoreRevocationOutcome>(
                    new InvalidOperationException(
                        "Synthetic uncertain outcome"))
                : Task.FromResult(
                    AiRestoreRevocationOutcome.AlreadyReconciled)
        };
        var service = new AiRestoreRevocationService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Single(repo.Calls);

        Assert.Equal(
            AiRestoreRevocationOutcome.AlreadyReconciled,
            await service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal((RunId, CancellationToken.None), call));
    }

    [Fact]
    public async Task RepeatedRun_RequestsFreshRepositoryVerification()
    {
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(++attempts == 1
                ? AiRestoreRevocationOutcome.Reconciled
                : AiRestoreRevocationOutcome.AlreadyReconciled)
        };
        var service = new AiRestoreRevocationService(repo);

        Assert.Equal(
            AiRestoreRevocationOutcome.Reconciled,
            await service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal(
            AiRestoreRevocationOutcome.AlreadyReconciled,
            await service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal((RunId, CancellationToken.None), call));
    }

    [Fact]
    public async Task PreviousSuccess_CannotBypassLaterNotReadyOutcome()
    {
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(++attempts == 1
                ? AiRestoreRevocationOutcome.Reconciled
                : AiRestoreRevocationOutcome.NotReady)
        };
        var service = new AiRestoreRevocationService(repo);

        Assert.Equal(
            AiRestoreRevocationOutcome.Reconciled,
            await service.EnsureRevocationsReconciledAsync(RunId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureRevocationsReconciledAsync(RunId));

        Assert.Equal(2, repo.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulRun_DoesNotAuthorizeDifferentRun()
    {
        var otherRunId = Guid.NewGuid();

        var repo = new FakeRepository
        {
            Handler = (id, _) => id == RunId
                ? Task.FromResult(AiRestoreRevocationOutcome.Reconciled)
                : Task.FromException<AiRestoreRevocationOutcome>(
                    new UnauthorizedAccessException("Synthetic denial"))
        };
        var service = new AiRestoreRevocationService(repo);

        Assert.Equal(
            AiRestoreRevocationOutcome.Reconciled,
            await service.EnsureRevocationsReconciledAsync(RunId));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EnsureRevocationsReconciledAsync(otherRunId));

        Assert.Collection(repo.Calls,
            call => Assert.Equal(
                (RunId, CancellationToken.None), call),
            call => Assert.Equal(
                (otherRunId, CancellationToken.None), call));
    }

    // Synthetic repository; defaults to NotReady, never success.

    private sealed class FakeRepository : IAiRestoreRevocationRepository
    {
        public List<(Guid RunId, CancellationToken Token)> Calls { get; }
            = new();

        public Func<Guid, CancellationToken, Task<AiRestoreRevocationOutcome>>
            Handler
        { get; init; } = (_, _) => Task.FromResult(
                AiRestoreRevocationOutcome.NotReady);

        public Task<AiRestoreRevocationOutcome> EnsureRevocationsReconciledAsync(
            Guid recoveryRunId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((recoveryRunId, cancellationToken));

            return Handler(recoveryRunId, cancellationToken);
        }
    }
}
