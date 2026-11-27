using Andril.AI.Interfaces;

namespace Andril.AI.Services;

/// <summary>Explicit safe default until an actual transport/revocation/admission authority exists.</summary>
public sealed class UnavailableAiProviderDispatchBoundary : IAiProviderDispatchBoundary
{
    public Task<AiProviderDispatchReceipt> DispatchAsync(AiProviderDispatchBinding binding,
        Func<CancellationToken, Task> send, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AiProviderDispatchReceipt(AiProviderDispatchOutcome.Unavailable));
    }
}
