using Andril.AI.DTOs;

namespace Andril.AI.Interfaces;

// Proposed technical transport states, NOT severity classifications, reviewer grants or sanctions.
public enum AiSafetyStage { Unknown, UserInput, AssistantOutput }
public enum AiSafetyOutcome { Unknown, ClearedForFurtherChecks, WithholdPendingAssessment, SafetyResponseRequired, PolicyUnavailable }
public enum AiReviewOrigin { Unknown, UserReport, ValidatedFlag }
public enum AiReviewAcknowledgementOutcome { Unknown, Accepted, Queued, Denied, PolicyUnavailable, Uncertain }

/// <summary>Trusted persisted subject revision and request binding, not credentials. Gateway must
/// verify the message/content revision and CURRENT operation/attempt; no invented expiry duration.</summary>
public sealed record AiSafetySubject(AiGenerationAttemptReference Attempt, Guid MessageId,
    Guid ContentRevision, AiSafetyStage Stage, string RequestFingerprint);
public sealed record AiSafetyAssessmentRequest(AiSafetySubject Subject, string Text, string ContentDigest)
{
    public override string ToString() => "AI safety assessment request [private content omitted]";
}
public sealed record AiSafetyAssessmentResult(AiSafetySubject Subject, string ContentDigest,
    Guid AssessmentId, AiSafetyOutcome Outcome);

/// <summary>No raw history. Report trigger uses stable request ID; flag trigger uses validated
/// flag ID. Subject is absent for reports, which require no new generation or paid attempt.</summary>
public sealed record AiReviewRequestBinding(AiAuthorizedActor Actor, Guid ConversationId, Guid MessageId,
    Guid RequestId, AiReviewOrigin Origin, Guid TriggerId, AiSafetySubject? Subject = null);
public sealed record AiValidatedSafetyFlag(AiSafetySubject Subject, Guid FlagId, Guid EvidenceRevision,
    bool Validated, bool SeriousRoutingPolicyVerified, bool PurposeApproved,
    bool EvidenceHandlingPermissionVerified, bool LifecyclePermits);
/// <summary>Opaque minimal receipt, NOT reviewer access, human contact/review or emergency notice.</summary>
public sealed record AiReviewAcknowledgement(AiReviewRequestBinding Request,
    AiReviewAcknowledgementOutcome Outcome, Guid? ReceiptId = null);

/// <summary>M1/M6 integration only; no real implementation or permissive fallback supplied.
/// Every call independently verifies internal caller permissions, trusted identity, exact
/// account/profile/module/conversation/message ownership, Social adult opt-in, current revision,
/// operation/attempt, privacy applicable to the purpose, deletion/expiry/revocation and recovery.
/// References/labels/model text/previous success are never credentials. Validate content digest
/// against actual authorized subject content and current stored revision under protected access.
///
/// Assessment is NOT human-review authorization. Only authorized reports or validated serious
/// flags may originate cases. Ordinary detection and general admin grants never permit browsing.
/// Reports have their OWN approved privacy/ownership policy; no generation quota/budget requirement.
/// Report submission alone grants NO reviewer access. Flag validation must verify serious routing
/// policy, case-specific purpose and minimized evidence permissions; unavailable policy denies.
///
/// Routing/submission must recheck evidence and revocation in a transaction-sensitive boundary;
/// service-side checks cannot synchronize deletion/expiry/case creation. Stable request AND trigger
/// uniqueness, immutable conflict checks and durable receipts prevent duplicate cases. A repeated
/// flag under a different request ID must not create a second case. No automatic retry of uncertain
/// submissions. Accepted/queued receipts confirm only that specific action, never human review.
///
/// Disclose only authorized necessary case evidence; never whole history, unrelated conversations,
/// module identities, Diary or person-to-person messages. Case-specific reviewer eligibility,
/// purpose/access/audit controls require M2/M6 approval. No ordinary history retrieval or punitive
/// methods. Major sanctions require separate authorized human oversight. No emergency escalation,
/// retention exception, hold, safety threshold, emotional profile or CF-18 consent is created.
/// Preserve 14-day inactivity expiry, immediate revocation and 24-hour active purge target;
/// incident retention/legal basis/exceptions remain pending, not borrowed from consent audits.
/// Financial reconciliation survives withholding/deletion and is independent of these outcomes.
/// Never log text or private evidence in ordinary diagnostics. No claim of semantic safety QA.
/// </summary>
public interface IAiSafetyReviewGateway
{
    Task<AiSafetyAssessmentResult> AssessAsync(AiSafetyAssessmentRequest request, CancellationToken cancellationToken = default);
    Task<AiReviewAcknowledgement> SubmitUserReportAsync(AiReviewRequestBinding request, CancellationToken cancellationToken = default);
    Task<AiValidatedSafetyFlag?> ValidateFlagAsync(AiSafetySubject subject, Guid flagId, CancellationToken cancellationToken = default);
    Task<AiReviewAcknowledgement> RouteValidatedFlagAsync(AiReviewRequestBinding request, AiValidatedSafetyFlag evidence,
        CancellationToken cancellationToken = default);
}
