namespace Andril.AI.Interfaces;

public enum AiProviderDispatchOutcome { Unavailable, Denied, NotSent, Sent, Indeterminate }

public sealed record AiProviderDispatchBinding(AiAuthorizedActor Actor, Guid OperationId,
    AiAcknowledgementRevision AcknowledgementRevision)
{
    public override string ToString() => "AI dispatch binding [private references omitted]";
}

public sealed record AiProviderDispatchReceipt(AiProviderDispatchOutcome Outcome)
{
    public override string ToString() => $"AI dispatch outcome: {Outcome}";
}

/// <summary>
/// New TD-04 transport integration contract. NOT implemented by a journal claim or final recheck.
/// A concrete authority must enforce account/session/lifecycle/privacy/admission and exact
/// dispatch/revocation ordering, including claim-to-send, before invoking send at most once.
/// No database locks may span send. It owns transport-specific cancellation/unknown-send
/// evidence and must not replay. Sent requires that exact invocation to finish; callback
/// success alone is not proof of this contract. Unsupported transports must never invoke send.
/// Production policy/provider/quota choices are not supplied by this interface.
/// </summary>
public interface IAiProviderDispatchBoundary
{
    Task<AiProviderDispatchReceipt> DispatchAsync(AiProviderDispatchBinding binding,
        Func<CancellationToken, Task> send, CancellationToken cancellationToken = default);
}
