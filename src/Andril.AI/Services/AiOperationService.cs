using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Services;

/// <summary>Internal management only. No provider, automatic retry, message writes or DI registration.</summary>
public sealed class AiOperationService
{
    private readonly IAiActorAuthorization _authorization;
    private readonly AiPrivacyNoticeGuard _privacy;
    private readonly IAiOperationRepository _repository;

    public AiOperationService(IAiActorAuthorization authorization, AiPrivacyNoticeGuard privacy, IAiOperationRepository repository)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<AiOperationResult> AdmitAsync(AiOperationAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AiOperationPolicy.ValidateRequest(request);
        var actor = await ResolveAsync(request.Module, cancellationToken);
        await _privacy.RequireSameActorSatisfiedAsync(actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _repository.AdmitAsync(actor, request, cancellationToken);
        // A cancellation here does NOT establish that admission rolled back. Reconcile explicitly.
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateResult(result, new(actor, request.ConversationId, request.OperationId),
            AiOperationOutcome.Admitted, AiOperationOutcome.Existing);
    }

    public async Task<AiOperationResult> ReconcileAsync(AiOperationReference operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequireOwnerAsync(operation, cancellationToken);
        var result = await _repository.ReconcileAsync(operation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateResult(result, operation, AiOperationOutcome.AuthorizedStatus);
    }

    public async Task<AiOperationResult> PrepareManualRetryAsync(AiManualRetryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !request.ExplicitlyRequested || !AiOperationPolicy.ValidReference(request.Operation))
            throw new ArgumentException("An explicit valid manual retry is required.", nameof(request));
        var operation = request.Operation;
        await RequireOwnerAsync(operation, cancellationToken);
        await _privacy.RequireSameActorSatisfiedAsync(operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var status = await _repository.ReconcileAsync(operation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(status, operation, AiOperationOutcome.AuthorizedStatus);
        if (status.Outcome != AiOperationOutcome.AuthorizedStatus) return status;
        var decision = AiOperationPolicy.CanRetry(operation, status.Snapshot, true, true, true);
        if (decision != AiRetryDecision.Eligible)
            return new(decision == AiRetryDecision.ReconciliationRequired
                ? AiOperationOutcome.ReconciliationRequired : AiOperationOutcome.RetryBlocked);
        // Reauthorize again after potentially slow reconciliation; repository still checks atomically.
        await _privacy.RequireSameActorSatisfiedAsync(operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _repository.PrepareManualRetryAsync(operation, status.Snapshot!.Revision, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(result, operation, AiOperationOutcome.ReadyForManualRetry);
        if (result.Outcome == AiOperationOutcome.ReadyForManualRetry &&
            (result.Snapshot!.AdmittedAtUtc != status.Snapshot.AdmittedAtUtc || result.Snapshot.QuotaDay != status.Snapshot.QuotaDay ||
             result.Snapshot.Revision == status.Snapshot.Revision || result.Snapshot.AttemptId is not null)) throw InvalidResult();
        return result;
    }

    private async Task RequireOwnerAsync(AiOperationReference operation, CancellationToken token)
    {
        if (!AiOperationPolicy.ValidReference(operation)) throw new ArgumentException("Invalid AI operation reference.", nameof(operation));
        if (await ResolveAsync(operation.Actor.Module, token) != operation.Actor) throw new UnauthorizedAccessException("AI operation access denied.");
    }

    private async Task<AiAuthorizedActor> ResolveAsync(Andril.AI.ChatBox.AiModule module, CancellationToken token)
    {
        var actor = await _authorization.GetAuthorizedActorAsync(module, token);
        token.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidActor(actor) || actor!.Module != module) throw new UnauthorizedAccessException("AI operation access denied.");
        return actor;
    }

    private static AiOperationResult ValidateResult(AiOperationResult? result, AiOperationReference expected, params AiOperationOutcome[] allowed)
    {
        if (result is null || !Enum.IsDefined(result.Outcome) || result.Outcome == AiOperationOutcome.Unknown) throw InvalidResult();
        if (allowed.Contains(result.Outcome))
        {
            if (!AiOperationPolicy.ValidSnapshot(result.Snapshot) || result.Snapshot!.Operation != expected) throw InvalidResult();
            if (result.Outcome is AiOperationOutcome.Admitted or AiOperationOutcome.ReadyForManualRetry &&
                (result.Snapshot.Status != AiOperationStatus.Reserved || result.Snapshot.AttemptId is not null)) throw InvalidResult();
        }
        else if (result.Outcome is AiOperationOutcome.Admitted or AiOperationOutcome.Existing or
                 AiOperationOutcome.ReadyForManualRetry or AiOperationOutcome.AuthorizedStatus || result.Snapshot is not null) throw InvalidResult();
        return result;
    }

    private static InvalidOperationException InvalidResult() => new("AI operation outcome could not be verified.");
}
