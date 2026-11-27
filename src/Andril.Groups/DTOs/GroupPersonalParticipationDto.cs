using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;

namespace Andril.Groups.DTOs;

/// <summary>Minimal projection of the caller's authorized participation in an exact group.</summary>
public sealed class GroupPersonalParticipationDto
{
    public Guid GroupId { get; }
    public string Module { get; }
    public GroupKind GroupKind { get; }
    public GroupPersonalParticipationState State { get; }

    private GroupPersonalParticipationDto(AuthorizedGroupPersonalParticipationResult result)
        => (GroupId, Module, GroupKind, State) =
            (result.GroupId, result.Scope.Module, result.Scope.GroupKind, result.State);

    public static GroupPersonalParticipationDto FromAuthorizedResult(AuthorizedGroupPersonalParticipationResult result,
        GroupModuleScope expectedScope, Guid expectedGroupId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedScope);
        if (result.ActorProfileId == Guid.Empty || expectedGroupId == Guid.Empty || result.GroupId != expectedGroupId ||
            result.Scope != expectedScope || !Enum.IsDefined(result.State))
            throw new InvalidOperationException("Personal Group participation projection target is invalid.");

        return new GroupPersonalParticipationDto(result);
    }

    public override string ToString() => "Personal Group participation [private]";
}
