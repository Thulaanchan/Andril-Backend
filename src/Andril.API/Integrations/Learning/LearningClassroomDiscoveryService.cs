using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Services;
using Andril.Groups.Policies;

namespace Andril.API.Integrations.Learning;

public sealed record LearningClassroomResult(string Outcome, IReadOnlyList<GroupDiscoveryCardDto>? Classrooms = null)
{
    public override string ToString() => $"Learning Classroom outcome: {Outcome}";
}
/// <summary>Ordinary read-only Classroom discovery; no AI permission, joining or member/message disclosure.</summary>
public sealed class LearningClassroomDiscoveryService(IGroupActorContext actors, GroupDiscoveryService discovery, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly GroupModuleScope Scope = new("learning", GroupKind.Classroom);
    public async Task<LearningClassroomResult> SearchAsync(string? query, int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || query.Any(char.IsControl) || limit is < 1 or > 100) return new("Invalid");
        var actor = await actors.GetCurrentActorAsync("learning", cancellationToken);
        if (!GroupDiscoveryService.IsEligibleActor(actor, Scope, time.GetUtcNow())) return new("Denied");
        var result = await discovery.DiscoverAsync(actor!, Scope, query, limit, cancellationToken);
        if (result is null) return new("Unavailable");
        var fresh = await actors.GetCurrentActorAsync("learning", cancellationToken);
        if (!GroupDiscoveryService.IsEligibleActor(fresh, Scope, time.GetUtcNow()) || fresh!.ProfileId != actor!.ProfileId) return new("Denied");
        return new("Available", GroupDiscoveryCardDto.FromAuthorizedResult(result, Scope));
    }
}
