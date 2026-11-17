using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Registers explicit account-wide Admin authorization policies.
/// Does not grant permissions or enable Admin endpoints.
/// Module-scoped actions require separate resource authorization.
/// </summary>
public static class AdminPermissionPolicyRegistration
{
    public static void AddAccountWidePolicy(
        AuthorizationOptions options,
        string policyName,
        string permission)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(policyName))
            throw new ArgumentException(
                "Policy name is required.", nameof(policyName));

        if (string.IsNullOrWhiteSpace(permission))
            throw new ArgumentException(
                "Permission is required.", nameof(permission));

        var name = policyName.Trim();

        if (options.GetPolicy(name) is not null)
            throw new InvalidOperationException(
                "An authorization policy with this name already exists.");

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(
                new AdminPermissionRequirement(permission))
            .Build();

        options.AddPolicy(name, policy);
    }
}
