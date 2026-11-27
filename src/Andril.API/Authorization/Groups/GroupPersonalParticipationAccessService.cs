using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Services;

namespace Andril.API.Authorization.Groups;

public interface IGroupPersonalParticipationAccessService
{
    Task<GroupPersonalParticipationDto?> GetStatusAsync(GroupModuleScope scope, Guid groupId,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the authenticated caller for a personal participation read.</summary>
public sealed class GroupPersonalParticipationAccessService : IGroupPersonalParticipationAccessService
{
    private readonly IGroupActorContext _actors;
    private readonly GroupPersonalParticipationService _participation;
    private readonly TimeProvider _clock;

    public GroupPersonalParticipationAccessService(IGroupActorContext actors, GroupPersonalParticipationService participation,
        TimeProvider? clock = null) => (_actors, _participation, _clock) =
        (actors ?? throw new ArgumentNullException(nameof(actors)),
            participation ?? throw new ArgumentNullException(nameof(participation)), clock ?? TimeProvider.System);

    public async Task<GroupPersonalParticipationDto?> GetStatusAsync(GroupModuleScope scope, Guid groupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope is null || groupId == Guid.Empty) return null;

        try
        {
            var actor = await _actors.GetCurrentActorAsync(scope.Module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!GroupDiscoveryService.IsEligibleActor(actor, scope, _clock.GetUtcNow())) return null;

            var result = await _participation.GetStatusAsync(actor!, scope, groupId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null || result.ActorProfileId != actor!.ProfileId || result.Scope != scope ||
                result.GroupId != groupId) return null;

            var currentActor = await _actors.GetCurrentActorAsync(scope.Module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!GroupDiscoveryService.IsEligibleActor(currentActor, scope, _clock.GetUtcNow()) ||
                currentActor!.ProfileId != actor.ProfileId) return null;

            return GroupPersonalParticipationDto.FromAuthorizedResult(result, scope, groupId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
