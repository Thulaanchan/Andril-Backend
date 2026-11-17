using Andril.AI.DTOs;
using Andril.AI.Interfaces;

namespace Andril.AI.Policies;

// Proposed internal state model; not a database schema, provider permission or M5 algorithm.
public enum AiConsentState { Unknown, AwaitingConsent, GrantedUnused, Started, Declined, Expired, Revoked, Completed, Failed }
public enum AiConsentDecision { Unknown, Allow, NotNow }
public enum AiConsentOutcome
{
    Unknown, Offered, DecisionRecorded, Started, Status, Withdrawn, AlreadyRecorded,
    ContinuationVerified, Suppressed, Denied, Expired, Revoked, Conflict, ReconciliationRequired, CompletedWithdrawalPolicyPending
}
public enum AiConsentRevocationReason { Unknown, Withdrawal, Logout, ModuleSwitch, AccountChange, EligibilityLoss, ConversationDeletion, ConversationExpiry, AccountDeletion }
public sealed record AiConsentPurpose(string Id, string Version, string ScopeId);
/// <summary>Epochs refer to authoritative server session/module activation state, never client booleans.</summary>
public sealed record AiConsentReference(AiOperationReference Operation, AiConsentPurpose Purpose, Guid SessionEpoch, Guid ModuleEpoch);
public sealed record AiConsentLifecycle(AiOperationReference Operation, Guid SessionEpoch, Guid ModuleEpoch,
    DateTimeOffset NowUtc, bool SessionActive, bool ConversationAvailable, bool EligibilityVerified, bool RecoveryVerified);
/// <summary>Approved server template describes recommendation and current-conversation use;
/// choices are explicitly Allow / Not now. No actual context or recommendation content.</summary>
public sealed record AiConsentNotice(string Revision, string RecommendationDescription, string CurrentConversationUseDescription)
{
    public override string ToString() => "AI consent notice [descriptions omitted]";
}
public sealed record AiConsentSnapshot(AiConsentReference Reference, AiConsentNotice Notice, AiConsentState State,
    Guid Revision, DateTimeOffset OfferedAtUtc, DateTimeOffset? GrantedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null, Guid? AttemptId = null);
/// <summary>Successful repository results include a fresh post-action lifecycle snapshot;
/// service preflight time is not the authoritative timestamp of a later database commit.</summary>
public sealed record AiConsentResult(AiConsentOutcome Outcome, AiConsentSnapshot? Snapshot = null, AiConsentLifecycle? Lifecycle = null,
    Guid? RequestId = null);
public sealed record AiConsentSuppression(AiAuthorizedActor Actor, Guid ConversationId, Guid DeclinedOperationId);
public sealed record AiExplicitRecommendationRequest(AiConsentReference Reference, Guid UserEventId, bool Verified);
/// <summary>Proposed minimized metadata; exact identifier set needs M2/M6 privacy review.
/// No runtime authority, content, embeddings or inferred labels. Never exposed via chat APIs.</summary>
public sealed record AiConsentAuditEvent(Guid EventId, Guid OperationId, AiConsentDecision Decision, string PurposeVersion, DateTimeOffset OccurredAtUtc);

/// <summary>Pure decisions over TRUSTED UTC/lifecycle/evidence. Cannot authenticate sessions,
/// verify user interaction provenance, consume grants durably or authorize provider execution.</summary>
public static class AiContextualConsentPolicy
{
    public static TimeSpan UnusedLifetime => TimeSpan.FromMinutes(5);
    public static TimeSpan AuditLifetime => TimeSpan.FromDays(14);
    public static bool ValidPurpose(AiConsentPurpose? purpose) => purpose is not null &&
        !string.IsNullOrWhiteSpace(purpose.Id) && !string.IsNullOrWhiteSpace(purpose.Version) && !string.IsNullOrWhiteSpace(purpose.ScopeId);
    public static bool ValidReference(AiConsentReference? reference) => reference is not null &&
        AiOperationPolicy.ValidReference(reference.Operation) && ValidPurpose(reference.Purpose) &&
        reference.SessionEpoch != Guid.Empty && reference.ModuleEpoch != Guid.Empty;
    public static bool ValidDecision(AiConsentDecision decision) => decision is AiConsentDecision.Allow or AiConsentDecision.NotNow;
    public static DateTimeOffset UnusedExpiresAt(DateTimeOffset grantedAtUtc) => Add(grantedAtUtc, UnusedLifetime);
    public static DateTimeOffset AuditExpiresAt(DateTimeOffset occurredAtUtc) => Add(occurredAtUtc, AuditLifetime);
    private static DateTimeOffset Add(DateTimeOffset utc, TimeSpan duration)
    {
        if (!AiOperationPolicy.ValidUtc(utc) || utc > DateTimeOffset.MaxValue.Subtract(duration)) throw Invalid();
        return utc.Add(duration);
    }
    public static bool ValidLifecycle(AiConsentReference reference, AiConsentLifecycle? lifecycle) => ValidReference(reference) &&
        lifecycle is not null && lifecycle.Operation == reference.Operation && lifecycle.SessionEpoch == reference.SessionEpoch &&
        lifecycle.ModuleEpoch == reference.ModuleEpoch && AiOperationPolicy.ValidUtc(lifecycle.NowUtc) &&
        lifecycle.SessionActive && lifecycle.ConversationAvailable && lifecycle.EligibilityVerified && lifecycle.RecoveryVerified;

    public static bool ValidSnapshot(AiConsentSnapshot? snapshot)
    {
        if (snapshot is null || !ValidReference(snapshot.Reference) || snapshot.Notice is null ||
            string.IsNullOrWhiteSpace(snapshot.Notice.Revision) || string.IsNullOrWhiteSpace(snapshot.Notice.RecommendationDescription) ||
            string.IsNullOrWhiteSpace(snapshot.Notice.CurrentConversationUseDescription) || snapshot.Revision == Guid.Empty ||
            !AiOperationPolicy.ValidUtc(snapshot.OfferedAtUtc) || !Enum.IsDefined(snapshot.State)) return false;
        if (snapshot.GrantedAtUtc is { } granted && (!AiOperationPolicy.ValidUtc(granted) || granted < snapshot.OfferedAtUtc ||
            granted > DateTimeOffset.MaxValue.Subtract(UnusedLifetime))) return false;
        if (snapshot.StartedAtUtc is { } started && (snapshot.GrantedAtUtc is not { } grant ||
            !AiOperationPolicy.ValidUtc(started) || started < grant || started >= UnusedExpiresAt(grant) ||
            snapshot.AttemptId is null || snapshot.AttemptId == Guid.Empty)) return false;
        if (snapshot.StartedAtUtc is null && snapshot.AttemptId is not null) return false;
        return snapshot.State switch
        {
            AiConsentState.AwaitingConsent or AiConsentState.Declined => snapshot.GrantedAtUtc is null && snapshot.StartedAtUtc is null,
            AiConsentState.GrantedUnused or AiConsentState.Expired => snapshot.GrantedAtUtc is not null && snapshot.StartedAtUtc is null,
            AiConsentState.Started or AiConsentState.Completed or AiConsentState.Failed => snapshot.StartedAtUtc is not null,
            AiConsentState.Revoked => true,
            _ => false
        };
    }
    public static bool CurrentSnapshot(AiConsentSnapshot? snapshot, AiConsentReference expected, AiConsentLifecycle? lifecycle) =>
        ValidSnapshot(snapshot) && snapshot!.Reference == expected && ValidLifecycle(expected, lifecycle) &&
        snapshot.OfferedAtUtc <= lifecycle!.NowUtc && (snapshot.GrantedAtUtc is null || snapshot.GrantedAtUtc <= lifecycle.NowUtc) &&
        (snapshot.StartedAtUtc is null || snapshot.StartedAtUtc <= lifecycle.NowUtc);
    public static bool CanStart(AiConsentSnapshot? snapshot, AiConsentReference expected, AiConsentLifecycle? lifecycle) =>
        CurrentSnapshot(snapshot, expected, lifecycle) && snapshot!.State == AiConsentState.GrantedUnused &&
        lifecycle!.NowUtc < UnusedExpiresAt(snapshot.GrantedAtUtc!.Value);
    public static bool CanContinue(AiConsentSnapshot? snapshot, AiConsentReference expected, AiConsentLifecycle? lifecycle, Guid attemptId, Guid revision) =>
        CurrentSnapshot(snapshot, expected, lifecycle) && snapshot!.State == AiConsentState.Started &&
        attemptId != Guid.Empty && snapshot.AttemptId == attemptId && revision != Guid.Empty && snapshot.Revision == revision;

    /// <summary>Suppression applies only to this conversation/identity, never a global refusal
    /// profile. A trusted explicit NEW request must name a different operation; it is not Allow.</summary>
    public static bool CanOffer(AiConsentReference reference, AiConsentLifecycle lifecycle,
        AiConsentSuppression? suppression, AiExplicitRecommendationRequest? newRequest)
    {
        if (!ValidLifecycle(reference, lifecycle)) return false;
        if (suppression is null) return true;
        if (!AiOperationPolicy.ValidActor(suppression.Actor) || suppression.ConversationId == Guid.Empty || suppression.DeclinedOperationId == Guid.Empty) return false;
        if (suppression.Actor != reference.Operation.Actor || suppression.ConversationId != reference.Operation.ConversationId) return true;
        return newRequest is not null && newRequest.Verified && newRequest.UserEventId != Guid.Empty && newRequest.Reference == reference &&
            reference.Operation.OperationId != suppression.DeclinedOperationId;
    }

    /// <summary>Structural transitions; repository additionally verifies interaction, lifecycle,
    /// revision CAS and trusted transition time. Replays have no new processing side effects.</summary>
    public static bool CanTransition(AiConsentSnapshot before, AiConsentSnapshot after)
    {
        if (!ValidSnapshot(before) || !ValidSnapshot(after) || before.Reference != after.Reference || before.Notice != after.Notice ||
            before.OfferedAtUtc != after.OfferedAtUtc) return false;
        if (before == after) return true;
        if (before.Revision == after.Revision) return false;
        if (before.State == AiConsentState.AwaitingConsent)
            return after.State is AiConsentState.GrantedUnused or AiConsentState.Declined ||
                (after.State == AiConsentState.Revoked && after.GrantedAtUtc is null && after.StartedAtUtc is null);
        if (before.GrantedAtUtc != after.GrantedAtUtc) return false;
        if (before.State == AiConsentState.GrantedUnused)
            return after.State == AiConsentState.Started ||
                (after.State is AiConsentState.Expired or AiConsentState.Revoked && after.StartedAtUtc is null);
        if (before.StartedAtUtc != after.StartedAtUtc || before.AttemptId != after.AttemptId) return false;
        return before.State == AiConsentState.Started && after.State is AiConsentState.Completed or AiConsentState.Failed or AiConsentState.Revoked;
    }

    public static bool CanReadAudit(AiConsentAuditEvent? audit, DateTimeOffset trustedNowUtc)
    {
        if (audit is null || audit.EventId == Guid.Empty || audit.OperationId == Guid.Empty || !ValidDecision(audit.Decision) ||
            string.IsNullOrWhiteSpace(audit.PurposeVersion) || !AiOperationPolicy.ValidUtc(trustedNowUtc)) return false;
        try { return trustedNowUtc >= audit.OccurredAtUtc && trustedNowUtc < AuditExpiresAt(audit.OccurredAtUtc); }
        catch (InvalidOperationException) { return false; }
    }
    private static InvalidOperationException Invalid() => new("AI consent time evidence could not be verified.");
}
