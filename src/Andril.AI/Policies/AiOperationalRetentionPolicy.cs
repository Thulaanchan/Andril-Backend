using Andril.AI.DTOs;

namespace Andril.AI.Policies;

public enum AiOperationalCategory { Unknown, Quota, OperationIdempotency, Billing, SecurityAudit }
public abstract record AiOperationalScope;
public sealed record AiModuleOperationalScope(AiOperationReference Operation) : AiOperationalScope
{ public override string ToString() => "Module operational scope [identifiers omitted]"; }
public sealed record AiAccountOperationalScope(Guid AccountId) : AiOperationalScope
{ public override string ToString() => "Account aggregate scope [identifier omitted]"; }
public sealed record AiApplicationOperationalScope(Guid BudgetPoolId) : AiOperationalScope
{ public override string ToString() => "Application aggregate scope [identifier omitted]"; }
public sealed record AiRestrictedSecurityScope(Guid PartitionId, Guid? AccountId = null) : AiOperationalScope
{ public override string ToString() => "Restricted security scope [identifiers omitted]"; }
public sealed record AiOperationalRecordReference(Guid RecordId, AiOperationalCategory Category, AiOperationalScope Scope, Guid SourceRevision)
{ public override string ToString() => $"Operational record ({Category}) [identifiers omitted]"; }

/// <summary>Approval/anchor IDs are authoritative lookup references, NOT credentials or proof
/// of approval by themselves. Only the protected evidence reader/repository may establish trust.</summary>
public sealed record AiOperationalAnchorApproval(AiOperationalCategory Category, Guid PolicyId, string Revision, Guid ApprovalEvidenceId, Guid AnchorRuleId)
{ public override string ToString() => "Operational anchor approval [identifiers omitted]"; }
public sealed record AiOperationalAnchorEvidence(AiOperationalRecordReference Record, Guid PolicyId, string PolicyRevision,
    Guid ApprovalEvidenceId, Guid AnchorRuleId, DateTimeOffset AnchorUtc)
{ public override string ToString() => "Operational anchor evidence [identifiers omitted]"; }
[Flags]
public enum AiOperationalPendingWork
{
    None = 0, ActiveGeneration = 1, QuotaReservation = 2, BillingReconciliation = 4,
    UncertainProvider = 8, IdempotencyReconciliation = 16, AtomicCompletion = 32,
    UncertainCancellation = 64, RecoveryRevocation = 128
}
public enum AiOperationalResolutionState { Unknown, Unresolved, TerminalReconciled }
public sealed record AiOperationalResolutionEvidence(AiOperationalRecordReference Record, Guid EvidenceId,
    AiOperationalResolutionState State, AiOperationalPendingWork Pending)
{ public override string ToString() => $"Operational resolution ({State}, {Pending}) [identifiers omitted]"; }
public sealed record AiOperationalReplayEvidence(AiOperationalRecordReference Record, Guid EvidenceId,
    Guid ApprovedStrategyId, string StrategyRevision)
{ public override string ToString() => "Operational replay evidence [identifiers omitted]"; }
public enum AiOperationalAccountState { Unknown, NotApplicable, NoConfirmedDeletion, ConfirmedDeleted }
public enum AiOperationalDispositionKind { Unknown, Erase, Anonymize, RetainUntil }
/// <summary>Future independently approved disposition, never an erasure/legal policy invented here.
/// RetainUntilUtc is required for retention; authorization ValidUntilUtc is independently finite.</summary>
public sealed record AiOperationalDisposition(AiOperationalRecordReference Record, Guid ApprovalEvidenceId,
    string PolicyRevision, AiOperationalDispositionKind Kind, DateTimeOffset ValidUntilUtc, DateTimeOffset? RetainUntilUtc = null)
{ public override string ToString() => "Operational disposition [identifiers omitted]"; }
public sealed record AiOperationalErasureEvidence(AiOperationalRecordReference Record, Guid EvidenceId,
    AiOperationalAccountState State, AiOperationalDisposition? Disposition = null)
{ public override string ToString() => $"Operational erasure evidence ({State}) [identifiers omitted]"; }
/// <summary>Future approved narrow exception only: basis, exact fields, access, finite deadline,
/// policy revision and audit/review evidence all independently verified. No generic hold flag.</summary>
public sealed record AiOperationalExceptionEvidence(AiOperationalRecordReference Record, Guid ApprovalEvidenceId,
    Guid BasisId, Guid RetainedFieldSetId, Guid AccessPolicyId, Guid AuditReviewEvidenceId,
    string PolicyRevision, DateTimeOffset RetainUntilUtc)
{ public override string ToString() => "Operational exception evidence [identifiers omitted]"; }
public sealed record AiOperationalRetentionEvidence(AiOperationalAnchorApproval? Approval, AiOperationalAnchorEvidence? Anchor,
    AiOperationalResolutionEvidence? Resolution, AiOperationalReplayEvidence? Replay,
    AiOperationalErasureEvidence? Erasure, AiOperationalExceptionEvidence? Exception = null)
{ public override string ToString() => "Operational retention evidence [identifiers omitted]"; }
public enum AiOperationalRetentionOutcome
{
    Unknown, NotYetExpired, EligibleForCleanup, EligibleForAnonymization, DeferredUnresolvedOperation,
    DeferredReplayProtection, RetentionPolicyUnavailable, ErasureDispositionRequired, RestrictedRetentionException
}
/// <summary>Pure decision, NOT worker authorization. False access restriction never grants access.
/// ReviewRequired means escalate a bounded blocker through approved privacy/accounting workflows,
/// not silent indefinite retention. Neither decision releases budget/quota or permits execution.</summary>
public sealed record AiOperationalRetentionDecision(AiOperationalRetentionOutcome Outcome, DateTimeOffset? ExpiresAtUtc,
    bool OrdinaryAccessMustBeRestricted, bool ReviewRequired);

/// <summary>Pure prototype metadata policy. No operational anchor event is selected here.
/// Evidence must originate from protected authoritative policy/storage verification; public record
/// construction and matching IDs cannot authenticate it. Missing approval blocks destructive action.
/// Proposed boundary: ordinary age expires inclusively at verified anchor + category duration.
/// Conversation content and consent-event retention stay with their existing independent policies.</summary>
public static class AiOperationalRetentionPolicy
{
    private const AiOperationalPendingWork KnownPending = (AiOperationalPendingWork)255;
    public static TimeSpan Duration(AiOperationalCategory category) => category switch
    {
        AiOperationalCategory.Quota or AiOperationalCategory.OperationIdempotency => TimeSpan.FromDays(14),
        AiOperationalCategory.Billing or AiOperationalCategory.SecurityAudit => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
    public static bool ValidReference(AiOperationalRecordReference? record)
    {
        if (record is null || record.RecordId == Guid.Empty || record.SourceRevision == Guid.Empty ||
            !Enum.IsDefined(record.Category) || record.Category == AiOperationalCategory.Unknown) return false;
        return record.Scope switch
        {
            AiModuleOperationalScope module => AiOperationPolicy.ValidReference(module.Operation) && record.Category != AiOperationalCategory.SecurityAudit,
            AiAccountOperationalScope account => account.AccountId != Guid.Empty && record.Category == AiOperationalCategory.Quota,
            AiApplicationOperationalScope application => application.BudgetPoolId != Guid.Empty && record.Category == AiOperationalCategory.Billing,
            AiRestrictedSecurityScope security => security.PartitionId != Guid.Empty && security.AccountId != Guid.Empty && record.Category == AiOperationalCategory.SecurityAudit,
            _ => false
        };
    }

    public static DateTimeOffset CalculateExpiry(AiOperationalRecordReference record, AiOperationalAnchorApproval approval,
        AiOperationalAnchorEvidence anchor, DateTimeOffset trustedNowUtc)
    {
        if (!ValidReference(record) || approval is null || anchor is null || !AiOperationPolicy.ValidUtc(trustedNowUtc) ||
            approval.Category != record.Category || approval.PolicyId == Guid.Empty || string.IsNullOrWhiteSpace(approval.Revision) ||
            approval.ApprovalEvidenceId == Guid.Empty || approval.AnchorRuleId == Guid.Empty || anchor.Record != record ||
            anchor.PolicyId != approval.PolicyId || anchor.PolicyRevision != approval.Revision ||
            anchor.ApprovalEvidenceId != approval.ApprovalEvidenceId || anchor.AnchorRuleId != approval.AnchorRuleId ||
            !AiOperationPolicy.ValidUtc(anchor.AnchorUtc) || anchor.AnchorUtc > trustedNowUtc)
            throw new InvalidOperationException("Operational retention anchor could not be verified.");
        var duration = Duration(record.Category);
        if (anchor.AnchorUtc > DateTimeOffset.MaxValue.Subtract(duration))
            throw new InvalidOperationException("Operational retention expiry is outside supported time.");
        return anchor.AnchorUtc.Add(duration);
    }

    public static AiOperationalRetentionDecision Evaluate(AiOperationalRecordReference record,
        AiOperationalRetentionEvidence? evidence, DateTimeOffset trustedNowUtc)
    {
        AiOperationalRetentionDecision Review(AiOperationalRetentionOutcome outcome, DateTimeOffset? expiry = null) => new(outcome, expiry, true, true);
        if (!ValidReference(record) || evidence is null || !AiOperationPolicy.ValidUtc(trustedNowUtc))
            return Review(AiOperationalRetentionOutcome.RetentionPolicyUnavailable);
        DateTimeOffset expires;
        try { expires = CalculateExpiry(record, evidence.Approval!, evidence.Anchor!, trustedNowUtc); }
        catch (InvalidOperationException) { return Review(AiOperationalRetentionOutcome.RetentionPolicyUnavailable); }

        var erasure = evidence.Erasure;
        var identifiable = record.Scope is AiModuleOperationalScope or AiAccountOperationalScope ||
            record.Scope is AiRestrictedSecurityScope { AccountId: not null };
        if (erasure is null || erasure.Record != record || erasure.EvidenceId == Guid.Empty || !Enum.IsDefined(erasure.State) ||
            erasure.State == AiOperationalAccountState.Unknown ||
            (identifiable ? erasure.State == AiOperationalAccountState.NotApplicable : erasure.State != AiOperationalAccountState.NotApplicable))
            return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);

        var disposition = erasure.Disposition;
        var deleted = erasure.State == AiOperationalAccountState.ConfirmedDeleted;
        if (deleted && (disposition is null || disposition.Record != record || disposition.ApprovalEvidenceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(disposition.PolicyRevision) || !Enum.IsDefined(disposition.Kind) || disposition.Kind == AiOperationalDispositionKind.Unknown ||
            !AiOperationPolicy.ValidUtc(disposition.ValidUntilUtc) || disposition.ValidUntilUtc <= trustedNowUtc))
            return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);
        if (!deleted && disposition is not null) return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);

        var exception = evidence.Exception;
        if (exception is not null)
        {
            if (exception.Record != record || exception.ApprovalEvidenceId == Guid.Empty || exception.BasisId == Guid.Empty ||
                exception.RetainedFieldSetId == Guid.Empty || exception.AccessPolicyId == Guid.Empty || exception.AuditReviewEvidenceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(exception.PolicyRevision) || !AiOperationPolicy.ValidUtc(exception.RetainUntilUtc) || exception.RetainUntilUtc <= trustedNowUtc)
                return Review(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, expires);
            // Contradictory erasure/anonymization and retention evidence must be resolved, not prioritized silently.
            if (deleted && disposition!.Kind != AiOperationalDispositionKind.RetainUntil)
                return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);
        }
        if (deleted)
        {
            if (disposition!.Kind == AiOperationalDispositionKind.RetainUntil)
            {
                if (exception is null || disposition.RetainUntilUtc is not { } deadline || !AiOperationPolicy.ValidUtc(deadline) || deadline <= trustedNowUtc || deadline > disposition.ValidUntilUtc ||
                    (exception is not null && exception.RetainUntilUtc != deadline))
                    return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);
                return new(AiOperationalRetentionOutcome.RestrictedRetentionException, expires, true, true);
            }
            if (disposition.RetainUntilUtc is not null) return Review(AiOperationalRetentionOutcome.ErasureDispositionRequired, expires);
        }
        if (exception is not null) return new(AiOperationalRetentionOutcome.RestrictedRetentionException, expires, true, true);

        var resolution = evidence.Resolution;
        if (resolution is null || resolution.Record != record || resolution.EvidenceId == Guid.Empty ||
            resolution.State != AiOperationalResolutionState.TerminalReconciled || resolution.Pending != AiOperationalPendingWork.None ||
            (resolution.Pending & ~KnownPending) != 0)
            return Review(AiOperationalRetentionOutcome.DeferredUnresolvedOperation, expires);
        if (!deleted && trustedNowUtc < expires) return new(AiOperationalRetentionOutcome.NotYetExpired, expires, false, false);
        var replay = evidence.Replay;
        if (replay is null || replay.Record != record || replay.EvidenceId == Guid.Empty || replay.ApprovedStrategyId == Guid.Empty ||
            string.IsNullOrWhiteSpace(replay.StrategyRevision)) return Review(AiOperationalRetentionOutcome.DeferredReplayProtection, expires);
        return new(deleted && disposition!.Kind == AiOperationalDispositionKind.Anonymize
            ? AiOperationalRetentionOutcome.EligibleForAnonymization : AiOperationalRetentionOutcome.EligibleForCleanup, expires, true, false);
    }
}
