using Andril.Learning.Interfaces;
using Andril.Learning.Progress;

namespace Andril.API.Integrations.Learning;

/// <summary>
/// Actual current-owner XP read permission over the shared Common boundary. Production
/// earning policy is deliberately unconfigured; this grants no award/publication/adjustment
/// capability or cleanup-worker authority and does not convert another currency to XP.
/// </summary>
public sealed class CurrentLearningXpReadAuthority(ICurrentLearningOwnerBoundary owners):ILearningXpAuthority,ILearningXpPolicyAuthority
{
    private sealed record ReadAllowed(bool Allowed);
    public async Task<bool> MayReadAsync(LearningSetupScope scope,CancellationToken cancellationToken=default)
        => (await owners.WithCurrentOwnerAsync<ReadAllowed>((owner,_)=>Task.FromResult<ReadAllowed?>(new(owner.ProfileId==scope.LearningProfileId)),cancellationToken,scope.AccountId))?.Allowed==true;
    public Task<LearningXpAwardEvidence?> ResolveAwardAsync(LearningSetupScope scope,LearningXpSource source,CancellationToken cancellationToken=default)
    {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<LearningXpAwardEvidence?>(null);}
    public Task<bool> RevalidateAwardAsync(LearningSetupScope scope,LearningXpAwardEvidence evidence,CancellationToken cancellationToken=default)
    {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(false);}
    public Task<LearningXpPolicy?> ReadCurrentPolicyAsync(CancellationToken cancellationToken=default)
    {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<LearningXpPolicy?>(LearningXpPolicy.Unconfigured);}
}
