using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;

namespace Andril.Groups.DTOs;

/// <summary>
/// Minimal ordinary-discovery projection, not an HTTP endpoint or AI disclosure permission.
/// Only source-backed GroupEntry fields are exposed. Membership/owner/evidence data never travels.
/// A projected snapshot is not a reusable authorization token for subsequent operations.
/// </summary>
public sealed class GroupDiscoveryCardDto
{
    public Guid Id { get; }
    public string Module { get; }
    public GroupKind GroupKind { get; }
    public string Name { get; }

    private GroupDiscoveryCardDto(GroupEntry group)
        => (Id, Module, GroupKind, Name) = (group.Id, group.Scope.Module, group.Scope.GroupKind, group.Name);

    /// <summary>Project only a result released by the authorized application read boundary.</summary>
    public static IReadOnlyList<GroupDiscoveryCardDto> FromAuthorizedResult(
        AuthorizedGroupDiscoveryResult result, GroupModuleScope expectedScope)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedScope);
        if (result.ActorProfileId == Guid.Empty || result.Scope != expectedScope ||
            result.Groups.Any(group => group.Scope != expectedScope || group.Id == Guid.Empty))
            throw new InvalidOperationException("Group discovery projection scope is invalid.");
        return Array.AsReadOnly(result.Groups.Select(group => new GroupDiscoveryCardDto(group)).ToArray());
    }

    public override string ToString() => "Group discovery card [private]";
}
