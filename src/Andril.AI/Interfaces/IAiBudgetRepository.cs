using Andril.AI.DTOs;
using Andril.AI.Policies;

namespace Andril.AI.Interfaces;

/// <summary>Trusted server evidence boundary, no implementation/default allow behavior.
/// Resolve evidence IDs against immutable server records, never accept client token/pricing flags.
/// Validate full exact request/framing/history/model token upper bound, reasoning-output contract,
/// current pricing revision, supported text-only mode, operation admission/revision and server-issued
/// attempt binding. Retain exact measured payload binding/provenance; detached token evidence fails.
/// Resolve complete approved pricing coverage for every supported category, including cache writes;
/// missing approval is Unknown/Incomplete, never ApprovedComplete. Existing two-rate estimation is
/// usable only if the approved configuration establishes those rates as full upper bounds.
/// Use trusted UTC configuration/clock; no hardcoded deployment dates.</summary>
public interface IAiBudgetEvidenceReader
{
    Task<AiBudgetPreparation?> ReadPreparationAsync(AiGenerationAttemptReference attempt, Guid evidenceId, CancellationToken cancellationToken = default);

    /// <summary>SEPARATE SYSTEM SECURITY BOUNDARY: independently authorize the current internal
    /// accounting caller and verify provider execution/usage/pricing/invoice authenticity. Deny
    /// unknown caller/evidence; ID is not a credential. Must work after user deletion/revocation,
    /// with no content resurrection. No permissive system authorization placeholder is supplied.
    /// Final actual USD must include all authoritative billable usage, including reasoning;
    /// validate provider calculations/currency and cumulative finality, not requested token limits.
    /// Preserve actual input/output/total, nullable cached/reasoning/cache-write/TTL observations.
    /// Independently resolve complete accounting coverage; parser observations never establish it.
    /// Unknown/unsupported billable categories must retain exposure, not finalize incomplete bills.</summary>
    Task<AiProviderBillingEvidence?> ReadSystemBillingAsync(Guid evidenceId, CancellationToken cancellationToken = default);
}

/// <summary>Proposed M2 PostgreSQL transactional boundary. Estimation is NOT reservation; result
/// is NOT dispatch permission. Recheck authoritative account/profile/module/conversation eligibility,
/// Social age/opt-in, privacy, recovery fence, operation/quota admission, exact bound attempt,
/// current complete token measurement, pricing/window, and shared application balance atomically.
/// Serialize all accounts/modules against ONE window pool; never check balance then insert separately.
/// Unique attempt binding + immutable comparison prevents duplicate exposure. Replay returns exact
/// original reservation (possibly settled/pending); changed details conflict, never dispatch again.
/// Existing IAiOperationRepository has a budget-evidence prerequisite but no common transaction or
/// pre-attempt reservation binding API. M2 must approve integrated admission/attempt issuance/transfer
/// before production use. Do not call the two repositories as independent competing admissions.
/// This batch requires an already durably issued attempt; no logical reservation is created here.
///
/// System settlement must independently verify internal caller and trusted provider evidence again,
/// lock original reservation/window, persist actual cost and remove held exposure exactly once.
/// Record real over-reservation/over-cap charges; deny later admission, never clamp invoices.
/// Unknown billing retains exposure. Reconciliation works after window end/account deletion without
/// granting user access, altering daily quota, restoring content or retrying execution.
/// Enforce event AND provider execution uniqueness; compare full immutable report, including usage.
/// Conflicts/adjustments require explicit reconciliation, not an extra charge or blind replacement.
/// Durable warning deduplication and financial retention need M2 review. No raw message content.
/// </summary>
public interface IAiBudgetRepository
{
    Task<AiBudgetResult> ReserveAsync(AiBudgetPreparation evidence, decimal estimatedUsd, CancellationToken cancellationToken = default);
    /// <summary>Fresh scope authorization; status does not allocate capacity or require new budget.</summary>
    Task<AiBudgetResult> ReadStatusAsync(AiGenerationAttemptReference attempt, CancellationToken cancellationToken = default);
    Task<AiBudgetResult> ReconcileSystemAsync(AiProviderBillingEvidence evidence, CancellationToken cancellationToken = default);
}
