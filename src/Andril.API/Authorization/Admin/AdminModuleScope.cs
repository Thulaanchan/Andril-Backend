namespace Andril.API.Authorization.Admin;

/// <summary>
/// Validates the canonical ANDRIL product-module identifier.
/// This type does not grant or verify Admin permissions.
/// </summary>
public static class AdminModuleScope
{
    public const string Learning = "learning";
    public const string Hobbies = "hobbies";
    public const string Social = "social";

    public static bool IsValid(string? moduleScope)
    {
        return moduleScope is Learning or Hobbies or Social;
    }

    public static string RequireValid(string? moduleScope)
    {
        if (string.IsNullOrWhiteSpace(moduleScope))
        {
            throw new ArgumentException(
                "A module scope is required.",
                nameof(moduleScope));
        }

        if (!IsValid(moduleScope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(moduleScope),
                "Unsupported ANDRIL module scope.");
        }

        return moduleScope;
    }
}
