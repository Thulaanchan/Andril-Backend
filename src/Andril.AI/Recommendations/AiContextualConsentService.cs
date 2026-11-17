using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;

namespace Andril.AI.Recommendations;

/// <summary>Internal consent coordination, not context access or dispatch. No content, quota debit,
/// ranking, tools, audit retrieval or persistence. Atomic lifecycle/intent checks remain repository-owned.</summary>
public sealed class AiContextualConsentService
{
    private readonly IAiActorAuthorization _authorization;
    private readonly AiPrivacyNoticeGuard _privacy;
    private readonly IAiContextualConsentRepository _repository;
    public AiContextualConsentService(IAiActorAuthorization authorization, AiPrivacyNoticeGuard privacy, IAiContextualConsentRepository repository)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<AiConsentResult> ApplyAsync(AiConsentRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        var reference = request.Reference;
        var lifecycle = await ResolveAsync(reference, cancellationToken);
        if (request is AiConsentStartRequest)
        {
            await _privacy.RequireSameActorSatisfiedAsync(reference.Operation.Actor, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var result = await _repository.ApplyAsync(request, lifecycle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); // Unknown durable outcome; no automatic retry/refund.
        var allowed = request switch
        {
            AiConsentOfferRequest => AiConsentOutcome.Offered,
            AiConsentDecisionRequest => AiConsentOutcome.DecisionRecorded,
            AiConsentStartRequest => AiConsentOutcome.Started,
            _ => AiConsentOutcome.Withdrawn
        };
        ValidateResult(result, reference, lifecycle, request.RequestId, allowed, AiConsentOutcome.AlreadyRecorded);
        if (result.Outcome == AiConsentOutcome.Offered && result.Snapshot!.State != AiConsentState.AwaitingConsent) throw Invalid();
        if (result.Outcome == AiConsentOutcome.DecisionRecorded && request is AiConsentDecisionRequest decision &&
            result.Snapshot!.State != (decision.Decision == AiConsentDecision.Allow ? AiConsentState.GrantedUnused : AiConsentState.Declined)) throw Invalid();
        if (result.Outcome is AiConsentOutcome.Started or AiConsentOutcome.AlreadyRecorded && request is AiConsentStartRequest start)
        {
            // AlreadyRecorded remains reconciliation only, never another start/dispatch permission.
            if (result.Snapshot!.AttemptId != start.AttemptId) throw Invalid();
            if (result.Outcome == AiConsentOutcome.Started && !AiContextualConsentPolicy.CanContinue(result.Snapshot, reference, result.Lifecycle,
                start.AttemptId, result.Snapshot.Revision)) throw Invalid();
        }
        if (result.Outcome == AiConsentOutcome.Withdrawn && result.Snapshot!.State != AiConsentState.Revoked) throw Invalid();
        return result;
    }

    public async Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lifecycle = await ResolveAsync(reference, cancellationToken);
        var result = await _repository.ReadStatusAsync(reference, lifecycle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateResult(result, reference, lifecycle, null, AiConsentOutcome.Status);
    }

    public async Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pending is null || pending.ConsentRevision == Guid.Empty || pending.AttemptId == Guid.Empty) throw new ArgumentException("Invalid pending consent reference.", nameof(pending));
        var lifecycle = await ResolveAsync(pending.Reference, cancellationToken);
        await _privacy.RequireSameActorSatisfiedAsync(pending.Reference.Operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _repository.CheckPendingAsync(pending, lifecycle, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResult(result, pending.Reference, lifecycle, null, AiConsentOutcome.ContinuationVerified);
        if (result.Outcome == AiConsentOutcome.ContinuationVerified && !AiContextualConsentPolicy.CanContinue(result.Snapshot,
            pending.Reference, result.Lifecycle, pending.AttemptId, pending.ConsentRevision)) throw Invalid();
        return result;
    }

    public async Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence is null || evidence.EventId == Guid.Empty || !AiContextualConsentPolicy.ValidReference(evidence.Reference) ||
            !Enum.IsDefined(evidence.Reason) || evidence.Reason == AiConsentRevocationReason.Unknown) throw new ArgumentException("Invalid lifecycle event reference.", nameof(evidence));
        var result = await _repository.InvalidateFromSystemAsync(evidence, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null || result.Event != evidence || !Enum.IsDefined(result.Outcome) || result.Outcome == AiConsentSystemOutcome.Unknown) throw Invalid();
        return result;
    }

    private async Task<AiConsentLifecycle> ResolveAsync(AiConsentReference reference, CancellationToken token)
    {
        if (!AiContextualConsentPolicy.ValidReference(reference)) throw new ArgumentException("Invalid consent scope.", nameof(reference));
        var actor = await _authorization.GetAuthorizedActorAsync(reference.Operation.Actor.Module, token);
        token.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidActor(actor) || actor != reference.Operation.Actor) throw new UnauthorizedAccessException("AI consent access denied.");
        var lifecycle = await _repository.ReadLifecycleAsync(reference.Operation, token);
        token.ThrowIfCancellationRequested();
        if (!AiContextualConsentPolicy.ValidLifecycle(reference, lifecycle)) throw new UnauthorizedAccessException("AI consent lifecycle unavailable.");
        return lifecycle!;
    }
    private static void ValidateRequest(AiConsentRequest? request)
    {
        if (request is null || !AiContextualConsentPolicy.ValidReference(request.Reference) || request.RequestId == Guid.Empty) throw new ArgumentException("Invalid consent request.", nameof(request));
        var valid = request switch
        {
            AiConsentOfferRequest offer => offer.ExplicitNewUserRequestEvidenceId != Guid.Empty,
            AiConsentDecisionRequest decision => decision.OfferRevision != Guid.Empty && decision.DecisionEvidenceId != Guid.Empty && AiContextualConsentPolicy.ValidDecision(decision.Decision),
            AiConsentStartRequest start => start.ConsentRevision != Guid.Empty && start.AttemptId != Guid.Empty,
            AiConsentWithdrawalRequest => true,
            _ => false
        };
        if (!valid) throw new ArgumentException("Unverified consent action.", nameof(request));
    }
    private static AiConsentResult ValidateResult(AiConsentResult? result, AiConsentReference reference, AiConsentLifecycle lifecycle,
        Guid? requestId, params AiConsentOutcome[] allowed)
    {
        if (result is null || !Enum.IsDefined(result.Outcome) || result.Outcome == AiConsentOutcome.Unknown) throw Invalid();
        if (allowed.Contains(result.Outcome))
        {
            if (result.RequestId != requestId || !AiContextualConsentPolicy.CurrentSnapshot(result.Snapshot, reference, result.Lifecycle) ||
                result.Lifecycle!.NowUtc < lifecycle.NowUtc) throw Invalid();
        }
        else if (result.Outcome is not (AiConsentOutcome.Suppressed or AiConsentOutcome.Denied or AiConsentOutcome.Expired or AiConsentOutcome.Revoked or
            AiConsentOutcome.Conflict or AiConsentOutcome.ReconciliationRequired or AiConsentOutcome.CompletedWithdrawalPolicyPending) ||
            result.Snapshot is not null || result.Lifecycle is not null || result.RequestId is not null) throw Invalid();
        return result;
    }
    private static InvalidOperationException Invalid() => new("AI consent outcome could not be verified.");
}
