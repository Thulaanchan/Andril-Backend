using Andril.Groups.Policies;

namespace Andril.Groups.Entities;

/// <summary>
/// An immutable, compatible module and group-kind pair, not an authorization decision.
/// </summary>
public sealed record GroupModuleScope
{
    public string Module { get; }

    public GroupKind GroupKind { get; }

    public GroupModuleScope(string? module, GroupKind groupKind)
    {
        if (!GroupModuleScopePolicy.IsCompatible(module, groupKind))
        {
            throw new ArgumentException("The module and group kind must be compatible.");
        }

        Module = module!;
        GroupKind = groupKind;
    }
}
