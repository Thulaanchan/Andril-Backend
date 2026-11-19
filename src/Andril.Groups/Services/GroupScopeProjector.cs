using Andril.Groups.DTOs;
using Andril.Groups.Entities;

namespace Andril.Groups.Services;

/// <summary>
/// Filters and projects group entries by an explicit validated module scope.
/// Pure projection utility; does not perform authentication, authorization, or access control.
/// </summary>
public static class GroupScopeProjector
{
    public static IReadOnlyList<GroupEntryDto> Project(
        IEnumerable<GroupEntry>? entries,
        GroupModuleScope? targetScope)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(targetScope);

        var projected = new List<GroupEntryDto>();

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("Group entry collection cannot contain null elements.", nameof(entries));
            }

            if (entry.Scope == targetScope)
            {
                projected.Add(GroupEntryDto.FromEntry(entry));
            }
        }

        return projected.AsReadOnly();
    }
}
