using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Represents an Admin permission restricted to one ANDRIL module.
/// This requirement does not grant access or verify target ownership.
/// </summary>
public sealed class AdminModulePermissionRequirement
    : IAuthorizationRequirement
{
    public AdminModulePermissionRequirement(
        string permission, string moduleScope)
    {
        if (string.IsNullOrWhiteSpace(permission))
        {
            throw new ArgumentException(
                "Admin permission is required.",
                nameof(permission));
        }

        Permission = permission.Trim();
        ModuleScope = AdminModuleScope.RequireValid(moduleScope);
    }

    public string Permission { get; }

    public string ModuleScope { get; }
}
