using Andril.Groups.Entities;
using Andril.Groups.Policies;

namespace Andril.Groups.DTOs;

/// <summary>
/// Read-model DTO representing group identity and validated scope.
/// Does not convey authorization, private profiles, or membership state.
/// </summary>
public sealed record GroupEntryDto(
    Guid Id,
    string Module,
    GroupKind GroupKind,
    string Name)
{
    public static GroupEntryDto FromEntry(GroupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new GroupEntryDto(
            entry.Id,
            entry.Scope.Module,
            entry.Scope.GroupKind,
            entry.Name);
    }
}
