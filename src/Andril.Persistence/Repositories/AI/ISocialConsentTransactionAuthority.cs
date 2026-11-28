using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.Persistence.DbContexts;

namespace Andril.Persistence.Repositories.AI;

/// <summary>Supplies current authoritative evidence on the repository's existing PostgreSQL transaction.</summary>
public interface ISocialConsentTransactionAuthority
{
    /// <summary>Bind the verified actor and self-only conversation to live session, module, recovery and safety evidence.
    /// Hold the relevant authorization fence until commit. Resolve the approved notice for a supplied purpose.</summary>
    Task<SocialConsentAuthorityEvidence?> ResolveAsync(SocialDbContext database, AiOperationReference operation,
        AiConsentPurpose? purpose, CancellationToken token);
    Task<bool> VerifyNewRequestAsync(SocialDbContext database, AiConsentOfferRequest request, CancellationToken token);
    Task<bool> VerifyDecisionAsync(SocialDbContext database, AiConsentDecisionRequest request, CancellationToken token);

    /// <summary>Verify the server attempt and atomically reserve operation, quota and budget under the same transaction.
    /// This callback performs database work exclusively. It does not contact a provider.</summary>
    Task<bool> TryAdmitAttemptAsync(SocialDbContext database, AiConsentStartRequest request, CancellationToken token);

    /// <summary>Fence pending durable work and late-reply completion for this exact operation and attempt.</summary>
    Task<bool> FencePendingAsync(SocialDbContext database, AiConsentReference reference, Guid attemptId, CancellationToken token);
    Task<bool> VerifySystemEventAsync(SocialDbContext database, AiConsentSystemInvalidation evidence, CancellationToken token);
}

public sealed record SocialConsentAuthorityEvidence(AiConsentLifecycle Lifecycle, AiConsentNotice? ApprovedNotice);
