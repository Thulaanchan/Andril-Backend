namespace Andril.Common.Accounts.Eligibility;

/// <summary>Canonical gender values for new and updated demographic submissions.</summary>
public static class GenderInputPolicy
{
    public const int MaximumLength = 100;

    public static string Normalize(string? gender)
    {
        if (gender is not ("male" or "female" or "other" or "prefer_not_to_say"))
            throw new EligibilityValidationException();
        return gender;
    }
}
