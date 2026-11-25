using Andril.Communication.Chat;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Proposed internal integration seam; no authoritative implementation exists.
/// Resolve the verified subject to an existing internal account and its owned,
/// active, eligible private profile. Load the conversation and authorized
/// participation from authoritative sources, not caller-supplied participant data.
/// Recheck module-specific eligibility, blocks, revocation and (for match-based
/// Social) adult eligibility, opt-in and current mutual matching on every request.
/// Unresolved conversation kinds or module rules must not be marked eligible.
/// </summary>
internal interface IChatConversationAccessEvidenceReader
{
    /// <summary>Fresh operation evidence. Owner mappings remain pending. Never delegates to ReadAsync.</summary>
    Task<ChatOperationAccessEvidence?> ReadOperationAsync(
        Guid verifiedSubjectId,
        ChatAuthorizationRequest request,
        Guid evaluationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ChatOperationAccessEvidence?>(null);
    }

    /// <summary>
    /// Freshly read all required evidence for this exact target and requestId.
    /// ObservedAtUtc must fall within this invocation and ValidUntilUtc must be
    /// authoritative; no arbitrary lifetime or clock tolerance is assumed here.
    /// IsCurrent requires coherent, non-stale evidence across the dependencies.
    /// Return null for missing/unavailable evidence. Never relabel cached evidence
    /// with a new requestId or convert missing checks to affirmative flags.
    /// Cancellation must propagate. This read alone cannot make a later write
    /// atomic with block/unmatch revocation; that integration remains pending.
    /// </summary>
    Task<ChatConversationAccessEvidence?> ReadAsync(
        Guid verifiedSubjectId,
        ChatModule module,
        Guid conversationId,
        Guid requestId,
        CancellationToken cancellationToken = default);
}
