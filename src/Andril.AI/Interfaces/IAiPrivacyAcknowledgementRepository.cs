using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// Server-owned current notice revision. A null result means policy authority is unavailable.
/// This does not establish lawful processing consent or external dispatch authority.
/// New TD-03 integration contract; no production policy source is implied by this interface.
/// </summary>
public interface IAiPrivacyPolicyVersionSource
{
    Task<string?> ReadCurrentVersionAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default);
}

public enum AiAcknowledgementOutcome { Available, Saved, Denied, Unavailable, Conflict, CommitUnknown }

public sealed record AiAcknowledgementRevision(Guid Generation, long Version);

public sealed record AiAcknowledgementState(
    string PolicyVersion, bool Acknowledged, AiAcknowledgementRevision Revision)
{
    public override string ToString() => "AI acknowledgement [private bindings and policy evidence omitted]";
}

public sealed record AiAcknowledgementResult(AiAcknowledgementOutcome Outcome, AiAcknowledgementState? State = null)
{
    public override string ToString() => $"AI acknowledgement outcome: {Outcome}";
}

/// <summary>
/// Exact account/profile/module-scoped notice evidence. Supplied actor IDs are claims to revalidate,
/// never authorization. Writes must be atomic and revision-aware; ambiguous commits are not replayed.
/// Recording notice acknowledgement alone MUST NOT satisfy IAiPrivacyNoticeGate: separately required
/// processing consent, current policy and coordinated dispatch remain independent gates.
/// </summary>
public interface IAiPrivacyAcknowledgementRepository
{
    Task<AiAcknowledgementResult> ReadAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default);
    Task<AiAcknowledgementResult> AcknowledgeAsync(AiAuthorizedActor actor, string reviewedPolicyVersion,
        Guid operationId, AiAcknowledgementRevision? expectedRevision, CancellationToken cancellationToken = default);
    Task<AiAcknowledgementResult> RevokeAsync(AiAuthorizedActor actor, Guid operationId,
        AiAcknowledgementRevision? expectedRevision, CancellationToken cancellationToken = default);
}
