namespace Andril.Common.Accounts.Eligibility;

public sealed record ModuleAgeEligibility(bool Learning, bool Hobbies, bool Social);

public static class AgeEligibilityPolicy
{
    public static int CalculateAge(DateOnly dateOfBirth, DateOnly utcDate)
    {
        // Npgsql treats DateOnly.MinValue as its negative-infinity sentinel.
        if (dateOfBirth == DateOnly.MinValue || dateOfBirth > utcDate) throw new EligibilityValidationException();
        var age = utcDate.Year - dateOfBirth.Year;
        // Calendar comparison intentionally makes Feb 29 mature on Mar 1 in non-leap years.
        if (utcDate.Month < dateOfBirth.Month || (utcDate.Month == dateOfBirth.Month && utcDate.Day < dateOfBirth.Day)) age--;
        return age;
    }

    public static ModuleAgeEligibility Evaluate(DateOnly dateOfBirth, DateOnly utcDate)
    {
        var age = CalculateAge(dateOfBirth, utcDate);
        return new(age >= 16, age >= 16, age >= 18);
    }
}
