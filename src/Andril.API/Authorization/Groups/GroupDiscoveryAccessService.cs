using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Services;

namespace Andril.API.Authorization.Groups;

public interface IGroupDiscoveryAccessService
{
    Task<IReadOnlyList<GroupDiscoveryCardDto>?> DiscoverAsync(GroupModuleScope scope, string searchText,
        int maximumCandidates, CancellationToken cancellationToken = default);
}

/// <summary>
/// Trusted, read-only application boundary. Never accepts caller-supplied actor identity.
/// The repository must independently fence current authorization through read/release.
/// No controller, endpoint registration or production provider is introduced here.
/// </summary>
public sealed class GroupDiscoveryAccessService : IGroupDiscoveryAccessService
{
    private readonly IGroupActorContext _actors;
    private readonly GroupDiscoveryService _discovery;
    private readonly TimeProvider _clock;

    public GroupDiscoveryAccessService(IGroupActorContext actors, GroupDiscoveryService discovery, TimeProvider? clock = null)
        => (_actors, _discovery, _clock) = (actors ?? throw new ArgumentNullException(nameof(actors)),
            discovery ?? throw new ArgumentNullException(nameof(discovery)), clock ?? TimeProvider.System);

    public async Task<IReadOnlyList<GroupDiscoveryCardDto>?> DiscoverAsync(GroupModuleScope scope, string searchText,
        int maximumCandidates, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(searchText);
        if (maximumCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        if (scope is null) return null;
        try
        {
            var actor = await _actors.GetCurrentActorAsync(scope.Module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!GroupDiscoveryService.IsEligibleActor(actor, scope, _clock.GetUtcNow())) return null;
            var result = await _discovery.DiscoverAsync(actor!, scope, searchText, maximumCandidates, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null || result.ActorProfileId != actor!.ProfileId || result.Scope != scope) return null;
            var cards = GroupDiscoveryCardDto.FromAuthorizedResult(result, scope);
            var currentActor = await _actors.GetCurrentActorAsync(scope.Module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return GroupDiscoveryService.IsEligibleActor(currentActor, scope, _clock.GetUtcNow()) &&
                currentActor!.ProfileId == actor.ProfileId ? cards : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Availability and authorization errors are non-disclosing; never return partial cards.
            return null;
        }
    }
}
