
using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Defines a permission required to access an Admin endpoint.
/// This requirement does not grant access by itself.
/// </summary>
public sealed class AdminPermissionRequirement
    : IAuthorizationRequirement
{
    public AdminPermissionRequirement(string permission)
    {
        if (string.IsNullOrWhiteSpace(permission))
        {
            throw new ArgumentException(
                "Admin permission must not be empty.",
                nameof(permission));
        }

        Permission = permission.Trim();
    }

    public string Permission { get; }
}
