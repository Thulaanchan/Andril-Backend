namespace Andril.Common.Accounts.Eligibility;

/// <summary>Technical input limits only. Approved gender vocabulary remains a separate policy decision.</summary>
public static class GenderInputPolicy
{
    public const int MaximumLength = 100;

    public static string Normalize(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender) || gender.Length > MaximumLength || gender.Any(char.IsControl))
            throw new EligibilityValidationException();
        return gender.Trim();
    }
}
