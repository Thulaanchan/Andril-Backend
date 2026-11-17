using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Registers explicit module-scoped Admin permission policies.
/// Does not grant permissions or verify target-resource ownership.
/// </summary>
public static class AdminModulePermissionPolicyRegistration
{
    public static void AddModulePolicy(
        AuthorizationOptions options,
        string policyName,
        string permission,
        string moduleScope)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(policyName))
        {
            throw new ArgumentException(
                "Policy name is required.",
                nameof(policyName));
        }

        if (string.IsNullOrWhiteSpace(permission))
        {
            throw new ArgumentException(
                "Permission is required.",
                nameof(permission));
        }

        var name = policyName.Trim();
        var validScope = AdminModuleScope.RequireValid(moduleScope);

        if (options.GetPolicy(name) is not null)
        {
            throw new InvalidOperationException(
                "An authorization policy with this name already exists.");
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(
                new AdminModulePermissionRequirement(
                    permission, validScope))
            .Build();

        options.AddPolicy(name, policy);
    }
}
