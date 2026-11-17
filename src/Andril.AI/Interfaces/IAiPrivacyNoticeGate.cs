
using System.Threading;
using System.Threading.Tasks;

namespace Andril.AI.Interfaces;

/// <summary>
/// Proposed internal gate for AI-PRIV-02. Evaluates the first-use
/// privacy notice and any legally required AI processing consent
/// for one independently authorized module identity.
/// Not a public endpoint or a consent-recording operation.
/// </summary>
public interface IAiPrivacyNoticeGate
{
    /// <summary>
    /// Evaluate the current server-authoritative policy and evidence.
    /// The actor must come from trusted backend authorization, never
    /// from unverified client input. Independently recheck account and
    /// module-profile eligibility, including Social age and opt-in,
    /// account revocation and applicable recovery restrictions.
    ///
    /// Verify notice acknowledgement for this exact account, profile,
    /// module and applicable policy revision. Do not reuse evidence
    /// from another module or a superseded profile. Verify separately
    /// any required affirmative processing consent and its validity.
    /// Acknowledgement alone is not proof of legally required consent.
    /// Material changes must follow the verified notification and
    /// renewal requirements before further processing is permitted.
    ///
    /// Only Satisfied is a positive gate outcome. If policy, lawful
    /// basis, required evidence or eligibility cannot be verified,
    /// fail closed. Propagate storage failures and cancellation.
    /// Do not return private evidence or log raw AI content.
    ///
    /// This check does not create a persistent permission or authorize
    /// CF-18 contextual recommendations. Recheck at admission and
    /// before provider dispatch with concurrency-safe revocation controls.
    /// Do not use this gate to block unrelated module features or
    /// otherwise authorized privacy settings and deletion requests.
    /// </summary>
    Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(
        AiAuthorizedActor actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal evaluation outcomes, not client authorization credentials.
/// Unknown and unrecognized values must never permit processing.
/// </summary>
public enum AiPrivacyNoticeGateOutcome
{
    Unknown = 0,
    Satisfied = 1,
    NoticeRequired = 2,
    ConsentRequired = 3,
    Denied = 4,
    PolicyNotVerified = 5
}
