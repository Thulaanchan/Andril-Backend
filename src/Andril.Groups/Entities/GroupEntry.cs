namespace Andril.Groups.Entities;

/// <summary>
/// Represents group identity and validated module scope.
/// Does not authorize group creation or member access.
/// </summary>
public sealed class GroupEntry
{
    public Guid Id { get; }

    public GroupModuleScope Scope { get; }

    public string Name { get; }

    public GroupEntry(Guid id, GroupModuleScope scope, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Group identifier is required.", nameof(id));
        }

        Scope = scope ?? throw new ArgumentNullException(nameof(scope));

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Group name is required.", nameof(name));
        }

        Id = id;
        Name = name;
    }
}
