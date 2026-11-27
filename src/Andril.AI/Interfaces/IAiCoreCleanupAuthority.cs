namespace Andril.AI.Interfaces;

/// <summary>Server-resolved maintenance scope; never constructed from an HTTP request.</summary>
public sealed record AiCoreCleanupScope(Guid Subject, Guid AccountId, Guid LearningProfileId)
{
    public override string ToString() => "AI maintenance scope [private references omitted]";
}

/// <summary>
/// Authoritative worker and retention boundary for the core PostgreSQL store only.
/// Implementations must independently authenticate the maintenance principal on every call.
/// The callback must run at most once under current, revocation-coordinated worker authority.
/// A missing worker grant or unverified scope must not invoke the callback.
/// Removal approval must cover the conversation header, messages and purge-work metadata,
/// legal holds and coordination with every attributable external content store. Unknown
/// retention/coverage must return false. Approval is not inferred from an overdue deadline.
/// No production authority implementation or permissive default is supplied here.
/// </summary>
public interface IAiCoreCleanupAuthority
{
    Task<T> WithWorkerScopeAsync<T>(
        Func<AiCoreCleanupScope, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);

    Task<bool> MayRemoveCoreAsync(AiConversationCleanupCandidate candidate,
        CancellationToken cancellationToken = default);
}
