using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Services;

/// <summary>Internal coordination only; never provider execution or standalone dispatch permission.</summary>
public sealed class AiBudgetService
{
    private readonly AiPrivacyNoticeGuard _privacy;
    private readonly IAiActorAuthorization _authorization;
    private readonly IAiBudgetEvidenceReader _evidence;
    private readonly IAiBudgetRepository _repository;

    public AiBudgetService(AiPrivacyNoticeGuard privacy, IAiActorAuthorization authorization,
        IAiBudgetEvidenceReader evidence, IAiBudgetRepository repository)
    {
        _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<AiBudgetResult> ReserveAsync(AiGenerationAttemptReference attempt, Guid evidenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireAttempt(attempt);
        if (evidenceId == Guid.Empty) throw new ArgumentException("Trusted evidence reference required.", nameof(evidenceId));
        await _privacy.RequireSameActorSatisfiedAsync(attempt.Operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = await _evidence.ReadPreparationAsync(attempt, evidenceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        AiBudgetPolicy.ValidatePreparation(evidence, attempt);
        if (evidence!.Mode == AiExecutionMode.Mock) return new(AiBudgetOutcome.MockNoSpend);
        var amount = AiBudgetPolicy.Estimate(evidence.Mode, evidence.Tokens, evidence.Pricing);
        await _privacy.RequireSameActorSatisfiedAsync(attempt.Operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _repository.ReserveAsync(evidence, amount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); // No implication of rollback/refund.
        Validate(result, attempt, AiBudgetOutcome.Reserved, AiBudgetOutcome.Existing);
        if (result.Outcome is AiBudgetOutcome.Reserved or AiBudgetOutcome.Existing &&
            (!AiBudgetPolicy.SameReservation(result.Reservation!, evidence) ||
             (result.Outcome == AiBudgetOutcome.Reserved && result.Reservation!.State != AiBillingState.Reserved))) throw Invalid();
        return result;
    }

    public async Task<AiBudgetResult> ReadStatusAsync(AiGenerationAttemptReference attempt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireAttempt(attempt);
        var actor = await _authorization.GetAuthorizedActorAsync(attempt.Operation.Actor.Module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidActor(actor) || actor != attempt.Operation.Actor) throw new UnauthorizedAccessException("AI budget access denied.");
        var result = await _repository.ReadStatusAsync(attempt, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Validate(result, attempt, AiBudgetOutcome.Status);
    }

    /// <summary>Not a user settlement endpoint. No arbitrary cost argument: trusted reader and
    /// repository BOTH independently authorize system caller and verify historical billing.</summary>
    public async Task<AiBudgetResult> ReconcileSystemAsync(Guid evidenceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (evidenceId == Guid.Empty) throw new ArgumentException("Trusted billing reference required.", nameof(evidenceId));
        var report = await _evidence.ReadSystemBillingAsync(evidenceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AiBudgetPolicy.ValidBilling(report)) throw Invalid();
        var result = await _repository.ReconcileSystemAsync(report!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(result, report!.Attempt, AiBudgetOutcome.Settled, AiBudgetOutcome.Existing, AiBudgetOutcome.Pending);
        if (result.Reservation is { } reservation)
        {
            if (reservation.Window.Id != report.WindowId || reservation.Model != report.Model || reservation.PricingRevision != report.PricingRevision) throw Invalid();
            if (result.Outcome == AiBudgetOutcome.Pending)
            {
                if (report.Kind != AiBillingEvidenceKind.Uncertain || reservation.State != AiBillingState.Pending) throw Invalid();
            }
            else if (report.Kind != AiBillingEvidenceKind.Final || reservation.State != AiBillingState.Settled ||
                reservation.FinalEvidence != report) throw Invalid();
        }
        return result;
    }

    private static AiBudgetResult Validate(AiBudgetResult? result, AiGenerationAttemptReference attempt, params AiBudgetOutcome[] allowed)
    {
        if (result is null || result.Outcome == AiBudgetOutcome.Unknown || !Enum.IsDefined(result.Outcome)) throw Invalid();
        if (allowed.Contains(result.Outcome))
        {
            if (!AiBudgetPolicy.ValidReservation(result.Reservation) || result.Reservation!.Attempt != attempt) throw Invalid();
        }
        else if (result.Outcome is not (AiBudgetOutcome.Denied or AiBudgetOutcome.Insufficient or AiBudgetOutcome.Conflict) || result.Reservation is not null) throw Invalid();
        return result;
    }
    private static void RequireAttempt(AiGenerationAttemptReference attempt)
    {
        if (!AiBudgetPolicy.ValidAttempt(attempt)) throw new ArgumentException("Invalid AI budget scope.", nameof(attempt));
    }
    private static InvalidOperationException Invalid() => new("AI budget outcome could not be verified.");
}
