
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;

namespace Andril.AI.Services;

/// <summary>
/// Internal completion orchestration for AI-OPS-01 and AI-OPS-04.
/// Validates input shape only; the repository must enforce fresh
/// authorization and atomic operation, message and quota persistence.
/// Do not register in production before those controls are verified.
/// </summary>
public sealed class AiLateReplyCommitService
{
    private readonly IAiLateReplyCommitRepository _repository;

    public AiLateReplyCommitService(
        IAiLateReplyCommitRepository repository)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Commit a reply for a previously admitted server-side dispatch.
    /// Identifiers and actor are not credentials. Never accept them
    /// directly from untrusted client or provider input.
    /// Success acknowledges persistence only, not delivery permission.
    /// </summary>
    public async Task<AiLateReplyCommitOutcome> CommitAsync(
        AiAuthorizedActor? dispatchedActor,
        Guid conversationId,
        Guid operationId,
        Guid generationAttemptId,
        Guid acceptedUserMessageId,
        string? assistantText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (dispatchedActor is null ||
            dispatchedActor.UserId == Guid.Empty ||
            dispatchedActor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(dispatchedActor.Module))
        {
            throw new UnauthorizedAccessException(
                "AI reply completion is not authorized.");
        }

        RequireId(conversationId, nameof(conversationId));
        RequireId(operationId, nameof(operationId));
        RequireId(generationAttemptId, nameof(generationAttemptId));
        RequireId(acceptedUserMessageId, nameof(acceptedUserMessageId));

        if (string.IsNullOrWhiteSpace(assistantText))
            throw new ArgumentException(
                "An assistant reply is required.", nameof(assistantText));

        // Do not perform a separate unprotected check and append.
        // The repository reauthorizes and verifies the current attempt,
        // lifecycle and recovery fence in the atomic commit boundary.
        // Preserve the exact reply and identifiers; no automatic retry.
        var outcome = await _repository.TryCommitAsync(
            dispatchedActor,
            conversationId,
            operationId,
            generationAttemptId,
            acceptedUserMessageId,
            assistantText,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        switch (outcome)
        {
            case AiLateReplyCommitOutcome.Committed:
            case AiLateReplyCommitOutcome.AlreadyCommitted:
                return outcome;

            case AiLateReplyCommitOutcome.NotEligibleOrMissing:
                throw new UnauthorizedAccessException(
                    "AI reply completion is not authorized.");

            default:
                throw new InvalidOperationException(
                    "AI reply completion could not be verified.");
        }
    }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "An identifier is required.", parameterName);
    }
}
