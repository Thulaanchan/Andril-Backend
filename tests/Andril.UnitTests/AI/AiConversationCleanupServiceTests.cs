
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Tests orchestration only; no real storage or worker authorization.
/// </summary>
public sealed class AiConversationCleanupServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_NullRepositoryThrows() =>
        Assert.Throws<ArgumentNullException>(() =>
            new AiConversationCleanupService(null!));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidLimit_DoesNotCallRepository(int limit)
    {
        var repo = new FakeRepository();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service(repo).RunBatchAsync(limit));

        Assert.Equal("limit", error.ParamName);
        Assert.Equal(0, repo.ListCalls);
        Assert.Empty(repo.Attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task EmptyBatch_ReturnsZeroCounts(int limit)
    {
        var repo = new FakeRepository();

        Assert.Equal(
            new AiConversationCleanupBatchResult(0, 0, 0, 0),
            await Service(repo).RunBatchAsync(limit));

        Assert.Equal(1, repo.ListCalls);
        Assert.Equal(limit, repo.LastLimit);
        Assert.Empty(repo.Attempts);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task NewlyRevokedCandidate_IsProcessedBeforeDeadline(
        AiModule module)
    {
        var row = Candidate(module, hoursAgo: 0);
        var repo = new FakeRepository(row);

        Assert.Equal(
            new AiConversationCleanupBatchResult(1, 1, 0, 0),
            await Service(repo).RunBatchAsync(1));

        Assert.Equal(row, Assert.Single(repo.Attempts));
    }

    [Fact]
    public async Task MixedOutcomes_AreCountedAndOrderedByDeadlineThenId()
    {
        var a = Candidate(AiModule.Learning, 30);
        var b = Candidate(AiModule.Hobbies, 30);
        var c = Candidate(AiModule.Social, 1);

        var repo = new FakeRepository(c, b, a)
        {
            Purge = (row, _) => Task.FromResult(row.Module switch
            {
                AiModule.Learning =>
                    AiConversationCleanupOutcome.Purged,

                AiModule.Hobbies =>
                    AiConversationCleanupOutcome.NotEligibleOrMissing,

                _ => AiConversationCleanupOutcome.DeferredByRetentionPolicy
            })
        };

        var expected = new[] { a, b }
            .OrderBy(r => r.ConversationId)
            .Append(c)
            .ToArray();

        Assert.Equal(
            new AiConversationCleanupBatchResult(3, 1, 1, 1),
            await Service(repo).RunBatchAsync(3));

        Assert.Equal<AiConversationCleanupCandidate>(
            expected, repo.Attempts);
    }

    [Fact]
    public async Task MaximumBatch_ProcessesExactlyOneHundredCandidates()
    {
        var rows = Enumerable.Range(0, 100)
            .Select(_ => Candidate())
            .ToArray();

        var repo = new FakeRepository(rows);

        Assert.Equal(
            new AiConversationCleanupBatchResult(100, 100, 0, 0),
            await Service(repo).RunBatchAsync(100));

        Assert.Equal(100, repo.Attempts
            .Select(r => r.ConversationId)
            .Distinct()
            .Count());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("conversation")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module-zero")]
    [InlineData("module-unknown")]
    [InlineData("default-revocation")]
    [InlineData("revocation-offset")]
    [InlineData("target-offset")]
    [InlineData("future-revocation")]
    [InlineData("early-target")]
    [InlineData("late-target")]
    public async Task MalformedCandidate_RejectsEntireBatchBeforePurge(
        string defect)
    {
        var good = Candidate();
        var row = Candidate();

        var bad = defect switch
        {
            "null" => null!,

            "conversation" =>
                row with { ConversationId = Guid.Empty },

            "account" =>
                row with { AccountId = Guid.Empty },

            "profile" =>
                row with { ModuleProfileId = Guid.Empty },

            "module-zero" =>
                row with { Module = (AiModule)0 },

            "module-unknown" =>
                row with { Module = (AiModule)99 },

            "default-revocation" =>
                row with { RevokedAtUtc = default },

            "revocation-offset" => row with
            {
                RevokedAtUtc = row.RevokedAtUtc.ToOffset(
                    TimeSpan.FromHours(1))
            },

            "target-offset" => row with
            {
                PurgeTargetUtc = row.PurgeTargetUtc.ToOffset(
                    TimeSpan.FromHours(1))
            },

            "future-revocation" => row with
            {
                RevokedAtUtc = Now.AddTicks(1),
                PurgeTargetUtc = Now.AddTicks(1).AddHours(24)
            },

            "early-target" => row with
            {
                PurgeTargetUtc = row.PurgeTargetUtc.AddTicks(-1)
            },

            "late-target" => row with
            {
                PurgeTargetUtc = row.PurgeTargetUtc.AddTicks(1)
            },

            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var repo = new FakeRepository(good, bad);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(2));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task DuplicateId_RejectsEntireBatchEvenWithDifferentScope()
    {
        var row = Candidate();

        var repo = new FakeRepository(
            row,
            row with { AccountId = Guid.NewGuid() });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(2));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task OverLimitBatch_IsRejectedBeforePurge()
    {
        var repo = new FakeRepository(Candidate(), Candidate());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(1));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task NullBatch_IsRejected()
    {
        var repo = new FakeRepository
        {
            List = (_, _) =>
                Task.FromResult<
                    IReadOnlyList<AiConversationCleanupCandidate>>(null!)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(1));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task InvalidClock_IsRejectedBeforePurge()
    {
        var repo = new FakeRepository(Candidate());

        var service = new AiConversationCleanupService(
            repo, new FixedClock(default));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunBatchAsync(1));

        Assert.Empty(repo.Attempts);
    }

    [Theory]
    [InlineData(AiConversationCleanupOutcome.Unknown)]
    [InlineData((AiConversationCleanupOutcome)99)]
    public async Task UnknownOutcome_StopsBatch(
        AiConversationCleanupOutcome outcome)
    {
        var first = Candidate(hoursAgo: 30);

        var repo = new FakeRepository(Candidate(), first)
        {
            Purge = (_, _) => Task.FromResult(outcome)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(2));

        Assert.Equal(first, Assert.Single(repo.Attempts));
    }

    [Fact]
    public async Task ListingAuthorizationFailure_PropagatesWithoutPurge()
    {
        var repo = new FakeRepository
        {
            List = (_, _) =>
                Task.FromException<
                    IReadOnlyList<AiConversationCleanupCandidate>>(
                        new UnauthorizedAccessException("Denied"))
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Service(repo).RunBatchAsync(1));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task PurgeAuthorizationFailure_StopsRemainingCandidates()
    {
        var first = Candidate(hoursAgo: 30);

        var repo = new FakeRepository(Candidate(), first)
        {
            Purge = (_, _) =>
                Task.FromException<AiConversationCleanupOutcome>(
                    new UnauthorizedAccessException("Denied"))
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Service(repo).RunBatchAsync(2));

        Assert.Equal(first, Assert.Single(repo.Attempts));
    }

    [Fact]
    public async Task ListingStorageFailure_IsNotReportedAsEmptySuccess()
    {
        var repo = new FakeRepository
        {
            List = (_, _) =>
                Task.FromException<
                    IReadOnlyList<AiConversationCleanupCandidate>>(
                        new InvalidOperationException(
                            "Synthetic storage failure"))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(1));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task LaterPurgeFailure_NoSuccessfulBatchResultOrFurtherAttempts()
    {
        var first = Candidate(hoursAgo: 30);
        var second = Candidate(hoursAgo: 10);

        var repo = new FakeRepository(Candidate(), second, first)
        {
            Purge = (row, _) => row == first
                ? Task.FromResult(
                    AiConversationCleanupOutcome.Purged)
                : Task.FromException<AiConversationCleanupOutcome>(
                    new InvalidOperationException(
                        "Synthetic storage failure"))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(repo).RunBatchAsync(3));

        Assert.Equal<AiConversationCleanupCandidate>(
            new[] { first, second }, repo.Attempts);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotListOrPurge()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var repo = new FakeRepository(Candidate());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(repo).RunBatchAsync(1, cts.Token));

        Assert.Equal(0, repo.ListCalls);
        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task CancellationDuringListing_PreventsPurge()
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            List = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();

                return Task.FromResult<
                    IReadOnlyList<AiConversationCleanupCandidate>>(
                        new[] { Candidate() });
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(repo).RunBatchAsync(1, cts.Token));

        Assert.Empty(repo.Attempts);
    }

    [Fact]
    public async Task CancellationDuringPurge_StopsRemainingAttempts()
    {
        using var cts = new CancellationTokenSource();
        var first = Candidate(hoursAgo: 30);

        var repo = new FakeRepository(Candidate(), first)
        {
            Purge = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();

                return Task.FromResult(
                    AiConversationCleanupOutcome.Purged);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(repo).RunBatchAsync(2, cts.Token));

        Assert.Equal(first, Assert.Single(repo.Attempts));
    }

    // Synthetic test helpers

    private static AiConversationCleanupCandidate Candidate(
        AiModule module = AiModule.Learning,
        int hoursAgo = 1)
    {
        var revoked = Now.AddHours(-hoursAgo);

        return new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            module,
            revoked,
            revoked.AddHours(24));
    }

    private static AiConversationCleanupService Service(
        FakeRepository repo) =>
        new(repo, new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRepository(
        params AiConversationCleanupCandidate[] rows)
        : IAiConversationCleanupRepository
    {
        public int ListCalls { get; private set; }

        public int LastLimit { get; private set; }

        public List<AiConversationCleanupCandidate> Attempts { get; }
            = new();

        public Func<
            int,
            CancellationToken,
            Task<IReadOnlyList<AiConversationCleanupCandidate>>>?
            List
        { get; init; }

        public Func<
            AiConversationCleanupCandidate,
            CancellationToken,
            Task<AiConversationCleanupOutcome>>?
            Purge
        { get; init; }

        public Task<IReadOnlyList<AiConversationCleanupCandidate>>
            ListPendingAsync(
                int limit,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ListCalls++;
            LastLimit = limit;

            return List?.Invoke(limit, cancellationToken) ??
                Task.FromResult<
                    IReadOnlyList<AiConversationCleanupCandidate>>(rows);
        }

        public Task<AiConversationCleanupOutcome> TryPurgeAsync(
            AiConversationCleanupCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Attempts.Add(candidate);

            return Purge?.Invoke(candidate, cancellationToken) ??
                Task.FromResult(AiConversationCleanupOutcome.Purged);
        }
    }
}
