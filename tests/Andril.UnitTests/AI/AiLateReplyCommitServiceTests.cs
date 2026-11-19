
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic service tests only; no real authorization,
/// database or quota ledger.
/// </summary>
public sealed class AiLateReplyCommitServiceTests
{
    [Fact]
    public void Constructor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AiLateReplyCommitService(null!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module-zero")]
    [InlineData("module-unknown")]
    public async Task InvalidActor_IsDeniedBeforeRepositoryCall(
        string defect)
    {
        var request = Create();
        var actor = request.Actor!;

        var invalid = defect switch
        {
            "null" => null,
            "account" => actor with { UserId = Guid.Empty },
            "profile" => actor with { ModuleProfileId = Guid.Empty },
            "module-zero" => actor with { Module = (AiModule)0 },
            "module-unknown" => actor with { Module = (AiModule)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var repo = new FakeRepository();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Commit(repo, request with { Actor = invalid }));

        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData("conversationId")]
    [InlineData("operationId")]
    [InlineData("generationAttemptId")]
    [InlineData("acceptedUserMessageId")]
    public async Task EmptyIdentifier_IsRejected(string parameter)
    {
        var request = Create();

        var invalid = parameter switch
        {
            "conversationId" =>
                request with { ConversationId = Guid.Empty },

            "operationId" =>
                request with { OperationId = Guid.Empty },

            "generationAttemptId" =>
                request with { AttemptId = Guid.Empty },

            "acceptedUserMessageId" =>
                request with { UserMessageId = Guid.Empty },

            _ => throw new ArgumentOutOfRangeException(nameof(parameter))
        };

        var repo = new FakeRepository();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Commit(repo, invalid));

        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u2003")]
    public async Task BlankReply_IsRejected(string? text)
    {
        var repo = new FakeRepository();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Commit(repo, Create() with { Text = text }));

        Assert.Equal("assistantText", error.ParamName);
        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiModule.Learning, AiLateReplyCommitOutcome.Committed)]
    [InlineData(AiModule.Hobbies, AiLateReplyCommitOutcome.Committed)]
    [InlineData(AiModule.Social, AiLateReplyCommitOutcome.Committed)]
    [InlineData(AiModule.Learning, AiLateReplyCommitOutcome.AlreadyCommitted)]
    [InlineData(AiModule.Hobbies, AiLateReplyCommitOutcome.AlreadyCommitted)]
    [InlineData(AiModule.Social, AiLateReplyCommitOutcome.AlreadyCommitted)]
    public async Task AcceptedOutcome_ForwardsExactArguments(
        AiModule module, AiLateReplyCommitOutcome outcome)
    {
        using var cts = new CancellationTokenSource();

        var request = Create(module);

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(outcome)
        };

        Assert.Equal(outcome,
            await Commit(repo, request, cts.Token));

        Assert.Equal(
            (request, cts.Token),
            Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task DefaultToken_IsForwarded()
    {
        var request = Create();

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(
                AiLateReplyCommitOutcome.Committed)
        };

        Assert.Equal(
            AiLateReplyCommitOutcome.Committed,
            await Commit(repo, request));

        Assert.Equal(
            (request, CancellationToken.None),
            Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task RepeatedCompletion_RequestsFreshRepositoryVerification()
    {
        var request = Create();
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(++attempts == 1
                ? AiLateReplyCommitOutcome.Committed
                : AiLateReplyCommitOutcome.AlreadyCommitted)
        };

        var service = new AiLateReplyCommitService(repo);

        Assert.Equal(
            AiLateReplyCommitOutcome.Committed,
            await Invoke(service, request));

        Assert.Equal(
            AiLateReplyCommitOutcome.AlreadyCommitted,
            await Invoke(service, request));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal(
                (request, CancellationToken.None), call));
    }

    [Fact]
    public async Task NotEligibleOrMissing_IsDenied()
    {
        var repo = new FakeRepository();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Commit(repo, Create()));

        Assert.Equal(
            "AI reply completion is not authorized.",
            error.Message);

        Assert.Single(repo.Calls);
    }

    [Theory]
    [InlineData(AiLateReplyCommitOutcome.Unknown)]
    [InlineData((AiLateReplyCommitOutcome)(-1))]
    [InlineData((AiLateReplyCommitOutcome)99)]
    public async Task UnknownOutcome_FailsClosed(
        AiLateReplyCommitOutcome outcome)
    {
        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(outcome)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Commit(repo, Create()));

        Assert.Equal(
            "AI reply completion could not be verified.",
            error.Message);

        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task RepositoryAuthorizationFailure_PropagatesWithoutRetry()
    {
        var failure = new UnauthorizedAccessException(
            "Synthetic denial");

        var repo = new FakeRepository
        {
            Handler = (_, _) =>
                Task.FromException<AiLateReplyCommitOutcome>(
                    failure)
        };

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Commit(repo, Create()));

        Assert.Same(failure, error);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task StorageFailure_PropagatesWithoutRetry()
    {
        var failure = new InvalidOperationException(
            "Synthetic storage failure");

        var repo = new FakeRepository
        {
            Handler = (_, _) =>
                Task.FromException<AiLateReplyCommitOutcome>(
                    failure)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Commit(repo, Create()));

        Assert.Same(failure, error);
        Assert.Single(repo.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelled_DoesNotCallRepository(
        bool invalidActor)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = Create();

        if (invalidActor)
            request = request with { Actor = null };

        var repo = new FakeRepository();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Commit(repo, request, cts.Token));

        Assert.Empty(repo.Calls);
    }

    [Theory]
    [InlineData(AiLateReplyCommitOutcome.Committed)]
    [InlineData(AiLateReplyCommitOutcome.AlreadyCommitted)]
    [InlineData(AiLateReplyCommitOutcome.NotEligibleOrMissing)]
    public async Task CancellationDuringCommit_Propagates(
        AiLateReplyCommitOutcome outcome)
    {
        using var cts = new CancellationTokenSource();

        var request = Create();

        var repo = new FakeRepository
        {
            Handler = (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                cts.Cancel();

                return Task.FromResult(outcome);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Commit(repo, request, cts.Token));

        Assert.Equal(
            (request, cts.Token),
            Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task RepositoryCancellation_PropagatesWithoutRetry()
    {
        using var cts = new CancellationTokenSource();

        var repo = new FakeRepository
        {
            Handler = (_, token) =>
            {
                cts.Cancel();

                return Task.FromCanceled<AiLateReplyCommitOutcome>(
                    token);
            }
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Commit(repo, Create(), cts.Token));

        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task ReconciledRepeatAfterUncertainFailure_PreservesRequest()
    {
        var request = Create();
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => ++attempts == 1
                ? Task.FromException<AiLateReplyCommitOutcome>(
                    new InvalidOperationException(
                        "Synthetic uncertain outcome"))
                : Task.FromResult(
                    AiLateReplyCommitOutcome.AlreadyCommitted)
        };

        var service = new AiLateReplyCommitService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(service, request));

        Assert.Single(repo.Calls);

        // Simulate an explicit repeat after external status reconciliation.
        Assert.Equal(
            AiLateReplyCommitOutcome.AlreadyCommitted,
            await Invoke(service, request));

        Assert.Equal(2, repo.Calls.Count);

        Assert.All(repo.Calls, call =>
            Assert.Equal(
                (request, CancellationToken.None), call));
    }

    [Fact]
    public async Task StaleAttempt_RespectsRepositoryDenial()
    {
        var old = Create();

        var current = old with
        {
            AttemptId = Guid.NewGuid()
        };

        var repo = new FakeRepository
        {
            Handler = (request, _) => Task.FromResult(
                request == current
                    ? AiLateReplyCommitOutcome.Committed
                    : AiLateReplyCommitOutcome.NotEligibleOrMissing)
        };

        var service = new AiLateReplyCommitService(repo);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Invoke(service, old));

        Assert.Equal(
            AiLateReplyCommitOutcome.Committed,
            await Invoke(service, current));

        Assert.Collection(repo.Calls,
            call => Assert.Equal(
                (old, CancellationToken.None), call),
            call => Assert.Equal(
                (current, CancellationToken.None), call));
    }

    [Fact]
    public async Task PreviousSuccess_CannotBypassLaterDenial()
    {
        var request = Create();
        var attempts = 0;

        var repo = new FakeRepository
        {
            Handler = (_, _) => Task.FromResult(++attempts == 1
                ? AiLateReplyCommitOutcome.Committed
                : AiLateReplyCommitOutcome.NotEligibleOrMissing)
        };

        var service = new AiLateReplyCommitService(repo);

        Assert.Equal(
            AiLateReplyCommitOutcome.Committed,
            await Invoke(service, request));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Invoke(service, request));

        Assert.Equal(2, repo.Calls.Count);
    }

    // Test-only request and repository; defaults to denial.

    private sealed record Request(
        AiAuthorizedActor? Actor,
        Guid ConversationId,
        Guid OperationId,
        Guid AttemptId,
        Guid UserMessageId,
        string? Text);

    private static Request Create(
        AiModule module = AiModule.Learning) => new(
            new AiAuthorizedActor(
                Guid.NewGuid(), Guid.NewGuid(), module),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "  Synthetic reply — வணக்கம்\n  ");

    private static Task<AiLateReplyCommitOutcome> Commit(
        FakeRepository repo,
        Request request,
        CancellationToken token = default) =>
        Invoke(new AiLateReplyCommitService(repo), request, token);

    private static Task<AiLateReplyCommitOutcome> Invoke(
        AiLateReplyCommitService service,
        Request request,
        CancellationToken token = default) =>
        service.CommitAsync(
            request.Actor,
            request.ConversationId,
            request.OperationId,
            request.AttemptId,
            request.UserMessageId,
            request.Text,
            token);

    private sealed class FakeRepository : IAiLateReplyCommitRepository
    {
        public List<(Request Request, CancellationToken Token)>
            Calls
        { get; } = new();

        public Func<Request, CancellationToken,
            Task<AiLateReplyCommitOutcome>> Handler
        { get; init; }
            = (_, _) => Task.FromResult(
                AiLateReplyCommitOutcome.NotEligibleOrMissing);

        public Task<AiLateReplyCommitOutcome> TryCommitAsync(
            AiAuthorizedActor dispatchedActor,
            Guid conversationId,
            Guid operationId,
            Guid generationAttemptId,
            Guid acceptedUserMessageId,
            string assistantText,
            CancellationToken cancellationToken = default)
        {
            var request = new Request(
                dispatchedActor,
                conversationId,
                operationId,
                generationAttemptId,
                acceptedUserMessageId,
                assistantText);

            Calls.Add((request, cancellationToken));

            return Handler(request, cancellationToken);
        }
    }
}
