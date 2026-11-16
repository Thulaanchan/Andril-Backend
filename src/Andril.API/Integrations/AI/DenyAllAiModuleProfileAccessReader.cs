
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;

namespace Andril.API.Integrations.AI;

/// <summary>
/// Fail-closed placeholder for module-profile authorization until M2's
/// database-backed profile access contract and migration are available.
///
/// This type must never be treated as a working profile lookup. All access
/// requests are denied. Do not expose the AI HTTP endpoint while this
/// placeholder is in use.
/// </summary>
public sealed class DenyAllAiModuleProfileAccessReader
    : IAiModuleProfileAccessReader
{
    public Task<Guid?> FindAuthorizedProfileIdAsync(
        Guid accountId,
        AiModule module,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Never derive, generate, or assume a profile ID. This safe default
        // must be replaced by M2-reviewed database-backed authorization.
        return Task.FromResult<Guid?>(null);
    }
}
