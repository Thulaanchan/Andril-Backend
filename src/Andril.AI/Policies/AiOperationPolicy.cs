using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;

namespace Andril.AI.Policies;

/// <summary>Pure decisions over trusted backend evidence. Does not implement durable admission,
/// authentication, provider dispatch, or distributed counters. Technical choices require M2 review.</summary>
public static class AiOperationPolicy
{
    public const int TesterCount = 6;
    public const int DailySlots = 20;
    public const int AccountPerMinute = 3;
    public const int GlobalPerMinute = 10;
    public const int MaxTextElements = 500;

    public static bool ValidActor(AiAuthorizedActor? actor) => actor is not null &&
        actor.UserId != Guid.Empty && actor.ModuleProfileId != Guid.Empty && Enum.IsDefined(actor.Module);

    public static bool ValidReference(AiOperationReference? reference) => reference is not null &&
        ValidActor(reference.Actor) && reference.ConversationId != Guid.Empty && reference.OperationId != Guid.Empty;

    public static void ValidateRequest(AiOperationAdmissionRequest? request)
    {
        if (request is null || !Enum.IsDefined(request.Module) || request.ConversationId == Guid.Empty ||
            request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.UserText) ||
            new StringInfo(request.UserText).LengthInTextElements > MaxTextElements)
            throw new ArgumentException("Invalid AI operation request.", nameof(request));
    }

    public static bool ValidUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    public static bool ValidAllowlist(IReadOnlyList<Guid>? accounts) => accounts is not null &&
        accounts.Count == TesterCount && accounts.All(x => x != Guid.Empty) && accounts.Distinct().Count() == TesterCount;

    public static bool HasDailyCapacity(int reservations, int charges) =>
        reservations >= 0 && charges >= 0 && (long)reservations + charges < DailySlots;

    /// <summary>Rolling window convention: (now minus one minute, now], using trusted UTC.
    /// Earlier events are ignored; future/default/non-UTC events invalidate the entire snapshot.</summary>
    public static bool HasRateCapacity(IReadOnlyList<DateTimeOffset>? events, DateTimeOffset now, int limit)
    {
        if (!ValidUtc(now) || now < DateTimeOffset.MinValue.AddMinutes(1) ||
            events is null || limit is not (AccountPerMinute or GlobalPerMinute)) return false;
        return events.All(x => ValidUtc(x) && x <= now) && events.Count(x => x > now.AddMinutes(-1)) < limit;
    }

    public static AiOperationOutcome DecideAdmission(AiAuthorizedActor actor, AiAdmissionEvidence? evidence)
    {
        if (!ValidActor(actor) || evidence is null || !evidence.EligibilityVerified || !ValidUtc(evidence.NowUtc) ||
            evidence.QuotaDay != DateOnly.FromDateTime(evidence.NowUtc.UtcDateTime) || !ValidAllowlist(evidence.AllowlistedAccounts))
            return AiOperationOutcome.Denied;
        if (!evidence.AllowlistedAccounts!.Contains(actor.UserId)) return AiOperationOutcome.NotAllowlisted;
        if (!evidence.BudgetAdmissionVerified) return AiOperationOutcome.BudgetNotVerified;
        if (evidence.Reservations < 0 || evidence.Charges < 0 ||
            (long)evidence.Reservations + evidence.Charges > DailySlots) return AiOperationOutcome.Denied;
        if (!HasDailyCapacity(evidence.Reservations, evidence.Charges)) return AiOperationOutcome.DailyQuotaExhausted;
        if (!HasRateCapacity(evidence.AccountAdmissions, evidence.NowUtc, AccountPerMinute)) return AiOperationOutcome.AccountRateLimited;
        if (!HasRateCapacity(evidence.GlobalAdmissions, evidence.NowUtc, GlobalPerMinute)) return AiOperationOutcome.GlobalRateLimited;
        return evidence.ConversationBusy ? AiOperationOutcome.ConversationBusy : AiOperationOutcome.Admitted;
    }

    /// <summary>Exact ordinal immutable input comparison; no silent whitespace normalization.
    /// Repository must compare only after fresh scope checks, before consuming capacity.</summary>
    public static AiOperationOutcome CompareRequests(AiOperationReference original, AiOperationAdmissionRequest stored,
        AiOperationReference incoming, AiOperationAdmissionRequest request)
    {
        ValidateRequest(stored);
        ValidateRequest(request);
        if (!ValidReference(original) || !ValidReference(incoming) || original != incoming || stored != request ||
            original.OperationId != stored.OperationId || original.ConversationId != stored.ConversationId ||
            original.Actor.Module != stored.Module) return AiOperationOutcome.IdempotencyConflict;
        return AiOperationOutcome.Existing;
    }

    public static bool ValidSnapshot(AiOperationSnapshot? state)
    {
        if (state is null || !ValidReference(state.Operation) || !ValidUtc(state.AdmittedAtUtc) ||
            state.QuotaDay != DateOnly.FromDateTime(state.AdmittedAtUtc.UtcDateTime) || state.Revision == Guid.Empty ||
            state.AttemptId == Guid.Empty || !Enum.IsDefined(state.Status) || !Enum.IsDefined(state.Quota) ||
            !Enum.IsDefined(state.Dispatch)) return false;
        if (state.Dispatch is AiDispatchState.Confirmed or AiDispatchState.Uncertain && state.AttemptId is null)
            return false;
        return state.Status switch
        {
            AiOperationStatus.Reserved => state.Quota == AiQuotaState.Reserved && state.Dispatch == AiDispatchState.NotDispatched,
            AiOperationStatus.InProgress => state.Quota == AiQuotaState.Reserved && state.Dispatch == AiDispatchState.Confirmed && state.AttemptId is not null,
            AiOperationStatus.PendingReconciliation => state.Quota == AiQuotaState.Reserved &&
                state.Dispatch is AiDispatchState.Uncertain or AiDispatchState.Confirmed or AiDispatchState.NotDispatched,
            AiOperationStatus.Completed => state.Quota == AiQuotaState.Committed && state.Dispatch == AiDispatchState.Confirmed && state.AttemptId is not null,
            AiOperationStatus.ConfirmedFailed => state.Quota is AiQuotaState.Reserved or AiQuotaState.Released && state.Dispatch == AiDispatchState.ConclusivelyFailed,
            AiOperationStatus.Cancelled => state.Quota == AiQuotaState.Committed && state.Dispatch == AiDispatchState.Confirmed && state.AttemptId is not null,
            _ => false
        };
    }

    /// <summary>Pure reconciliation proposal. Completion persistence remains exclusively with
    /// IAiLateReplyCommitRepository. Cancellation never establishes rollback or authorizes release.</summary>
    public static AiOperationSnapshot ApplyAccounting(AiOperationSnapshot state, AiAccountingEvent signal)
    {
        if (!ValidSnapshot(state) || !Enum.IsDefined(signal) || signal == AiAccountingEvent.Unknown)
            throw new InvalidOperationException("Unverified AI accounting transition.");
        if (signal == AiAccountingEvent.UsableCompletion)
        {
            if (state.Status == AiOperationStatus.Completed) return state;
            if (state.Status is not (AiOperationStatus.InProgress or AiOperationStatus.PendingReconciliation) ||
                state.Dispatch != AiDispatchState.Confirmed || state.AttemptId is null) throw InvalidTransition();
            return Validated(state with { Status = AiOperationStatus.Completed, Quota = AiQuotaState.Committed });
        }
        if (signal == AiAccountingEvent.ConfirmedUnrecoverableFailure)
        {
            // Caller must establish conclusive provider + storage evidence, not just rejection/timeout.
            if (state.Status != AiOperationStatus.ConfirmedFailed || state.Dispatch != AiDispatchState.ConclusivelyFailed)
                throw InvalidTransition();
            return Validated(state with { Quota = AiQuotaState.Released });
        }
        if (state.Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled or AiOperationStatus.ConfirmedFailed)
            return state; // Terminal accounting cannot be reopened by cancellation/uncertainty.
        if (signal == AiAccountingEvent.UserCancellation && state.Dispatch == AiDispatchState.Confirmed)
            return Validated(state with { Status = AiOperationStatus.Cancelled, Quota = AiQuotaState.Committed });
        return Validated(state with { Status = AiOperationStatus.PendingReconciliation }); // Retain reservation.
    }

    /// <summary>Structural transition checks only, requiring authoritative event evidence and
    /// a new durable revision. Does not itself prove dispatch, failure, budget or completion.</summary>
    public static bool CanTransition(AiOperationSnapshot before, AiOperationSnapshot after,
        AiConfirmedExecutionEvidence? confirmedExecution = null)
    {
        if (!ValidSnapshot(before) || !ValidSnapshot(after) || before.Operation != after.Operation ||
            before.AdmittedAtUtc != after.AdmittedAtUtc || before.QuotaDay != after.QuotaDay) return false;
        if (confirmedExecution is not null && !ValidConfirmation(before, after, confirmedExecution)) return false;
        if (before == after) return confirmedExecution is null; // Ordinary exact-state replay only.
        if (before.Revision == after.Revision) return false;
        if (before.Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled || before.Quota == AiQuotaState.Released) return false;
        if (before.Status == AiOperationStatus.ConfirmedFailed)
            return (after.Status == AiOperationStatus.ConfirmedFailed && after.Quota == AiQuotaState.Released && after.AttemptId == before.AttemptId) ||
                (after.Status == AiOperationStatus.Reserved && after.Quota == AiQuotaState.Reserved && after.AttemptId is null);
        if (before.Status == AiOperationStatus.Reserved && after.Status == AiOperationStatus.InProgress)
            // A reserved budget/attempt identity must survive dispatch unchanged. A legacy
            // no-attempt -> issued-attempt shape remains representable, never dispatch authority.
            return after.AttemptId is not null && (before.AttemptId is null || after.AttemptId == before.AttemptId);
        if (before.Status == AiOperationStatus.PendingReconciliation && before.Dispatch == AiDispatchState.Uncertain &&
            after.Dispatch == AiDispatchState.Confirmed && after.Status is AiOperationStatus.PendingReconciliation or AiOperationStatus.InProgress)
            return confirmedExecution is not null;
        if (after.Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled)
            return before.Dispatch == AiDispatchState.Confirmed && before.AttemptId is not null && before.AttemptId == after.AttemptId;
        if (after.Status == AiOperationStatus.ConfirmedFailed)
            return after.Quota == AiQuotaState.Reserved && after.AttemptId == before.AttemptId;
        return after.Status == AiOperationStatus.PendingReconciliation && after.AttemptId == before.AttemptId &&
            (after.Dispatch == before.Dispatch || after.Dispatch == AiDispatchState.Uncertain);
    }

    public static AiRetryDecision CanRetry(AiOperationReference expected, AiOperationSnapshot? state,
        bool explicitlyRequested, bool reconciliationVerified, bool eligibilityVerified)
    {
        if (!ValidReference(expected) || !explicitlyRequested || !eligibilityVerified) return AiRetryDecision.Blocked;
        if (!reconciliationVerified || !ValidSnapshot(state)) return AiRetryDecision.ReconciliationRequired;
        if (state!.Operation != expected) return AiRetryDecision.Blocked;
        if (state.Status == AiOperationStatus.PendingReconciliation || state.Dispatch == AiDispatchState.Uncertain)
            return AiRetryDecision.ReconciliationRequired;
        if (state.Status != AiOperationStatus.ConfirmedFailed) return AiRetryDecision.Blocked;
        return state.Quota == AiQuotaState.Released ? AiRetryDecision.CapacityContractReviewRequired : AiRetryDecision.Eligible;
    }

    private static InvalidOperationException InvalidTransition() => new("Unsupported AI accounting transition.");
    private static AiOperationSnapshot Validated(AiOperationSnapshot result) =>
        ValidSnapshot(result) ? result : throw InvalidTransition();

    private static bool ValidConfirmation(AiOperationSnapshot before, AiOperationSnapshot after, AiConfirmedExecutionEvidence evidence) =>
        before.Status == AiOperationStatus.PendingReconciliation && before.Dispatch == AiDispatchState.Uncertain &&
        evidence.Attempt is not null && evidence.Attempt.Operation == before.Operation &&
        before.AttemptId is { } attempt && evidence.Attempt.AttemptId == attempt && after.AttemptId == attempt &&
        evidence.ExpectedRevision == before.Revision && evidence.EvidenceId != Guid.Empty &&
        ValidUtc(evidence.ObservedAtUtc) && evidence.ObservedAtUtc >= before.AdmittedAtUtc &&
        after.Dispatch == AiDispatchState.Confirmed &&
        after.Status is AiOperationStatus.PendingReconciliation or AiOperationStatus.InProgress;
}
