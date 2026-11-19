namespace Andril.Groups.Policies;

public enum GroupKind
{
    Classroom = 1,
    Club = 2,
    Zone = 3
}

/// <summary>
/// Checks module and group-kind compatibility only.
/// Does not authenticate callers or authorize group access.
/// </summary>
public static class GroupModuleScopePolicy
{
    public static bool IsCompatible(string? module, GroupKind groupKind) =>
        (module, groupKind) switch
        {
            ("learning", GroupKind.Classroom) => true,
            ("hobbies", GroupKind.Club) => true,
            ("social", GroupKind.Zone) => true,
            _ => false
        };
}
