using Andril.Learning.Discovery;
using Andril.Learning.Interfaces;

namespace Andril.Learning.Services;

public enum LearningPeopleOutcome { Unavailable, Available, Denied, Invalid }
public sealed record LearningPersonCard(Guid ProfileId, string DisplayName)
{
    public override string ToString() => "Learning person [private content omitted]";
}
public sealed record LearningPeopleResult(LearningPeopleOutcome Outcome, IReadOnlyList<LearningPersonCard>? People = null)
{
    public override string ToString() => $"Learning People outcome: {Outcome}";
}
public sealed class LearningPeopleDiscoveryService(ILearningSetupAccessBoundary access, ILearningPeopleDiscoveryReader reader)
{
    public async Task<LearningPeopleResult> SearchAsync(string? query, int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Technical work bounds only, not a product quota or matching/ranking policy.
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || query.Any(char.IsControl) || limit is < 1 or > 100)
            return new(LearningPeopleOutcome.Invalid);
        var auth = await access.CheckAsync(LearningSetupOperation.ReadSetup, cancellationToken);
        if (auth.State == LearningSetupAccessState.Denied) return new(LearningPeopleOutcome.Denied);
        if (auth.State != LearningSetupAccessState.Allowed || auth.Scope is null) return new(LearningPeopleOutcome.Unavailable);
        LearningPeopleResult? projected = null; var calls = 0;
        var result = await reader.WithAuthorizedSearchAsync(auth.Scope, query, limit, async (snapshot, ct) =>
        {
            if (snapshot is null || snapshot.AccountId != auth.Scope.AccountId || snapshot.ProfileId != auth.Scope.LearningProfileId)
                return new LearningPeopleResult(LearningPeopleOutcome.Denied);
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref calls) != 1 || snapshot.AccountId != auth.Scope.AccountId || snapshot.ProfileId != auth.Scope.LearningProfileId ||
                string.IsNullOrWhiteSpace(snapshot.AuthorizationRevision) || snapshot.Candidates is null || snapshot.Candidates.Count > limit)
                return new LearningPeopleResult(LearningPeopleOutcome.Unavailable);
            var rejected = new HashSet<Guid>(); var evidence = new Dictionary<Guid, LearningPeopleEvidence>();
            foreach (var candidate in snapshot.Candidates)
            {
                if (candidate is null || candidate.ProfileId == Guid.Empty) continue;
                if (evidence.TryGetValue(candidate.ProfileId, out var prior))
                {
                    if (prior != candidate) rejected.Add(candidate.ProfileId);
                }
                else evidence.Add(candidate.ProfileId, candidate);
            }
            var cards = evidence.Values.Where(x => !rejected.Contains(x.ProfileId) && x.ProfileId != auth.Scope.LearningProfileId &&
                x.Module == "learning" && x.Lifecycle == LearningDiscoveryLifecycle.Active && x.Eligibility == LearningDiscoveryDecision.Allowed &&
                x.Visibility == LearningDiscoveryDecision.Allowed && x.Blocking == LearningDiscoveryDecision.Allowed &&
                x.OrdinaryDisplayNameDisclosure == LearningDiscoveryDecision.Allowed && !string.IsNullOrWhiteSpace(x.DisplayName))
                .Select(x => new LearningPersonCard(x.ProfileId, x.DisplayName!)).OrderBy(x => x.ProfileId).ToArray();
            var fresh = await access.CheckAsync(LearningSetupOperation.ReadSetup, ct);
            if (fresh.State != LearningSetupAccessState.Allowed || fresh.Scope?.AccountId != auth.Scope.AccountId || fresh.Scope.LearningProfileId != auth.Scope.LearningProfileId)
                return new LearningPeopleResult(fresh.State == LearningSetupAccessState.Denied ? LearningPeopleOutcome.Denied : LearningPeopleOutcome.Unavailable);
            projected = new(LearningPeopleOutcome.Available, Array.AsReadOnly(cards)); return projected;
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (calls != 1 || projected is null || !ReferenceEquals(projected, result)) return new(LearningPeopleOutcome.Unavailable);
        var final = await access.CheckAsync(LearningSetupOperation.ReadSetup, cancellationToken);
        if (final.State != LearningSetupAccessState.Allowed || final.Scope?.AccountId != auth.Scope.AccountId || final.Scope.LearningProfileId != auth.Scope.LearningProfileId)
            return new(final.State == LearningSetupAccessState.Denied ? LearningPeopleOutcome.Denied : LearningPeopleOutcome.Unavailable);
        return projected;
    }
}
