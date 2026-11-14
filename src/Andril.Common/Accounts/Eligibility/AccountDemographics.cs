namespace Andril.Common.Accounts.Eligibility;

/// <summary>Private self-reported data, never an API response or a public module profile.</summary>
public sealed class AccountDemographics
{
    private AccountDemographics() { }
    public Guid AccountId { get; private set; }
    public DateOnly DateOfBirth { get; private set; }
    public string Gender { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static AccountDemographics Create(Guid accountId, DateOnly dateOfBirth, string? gender, DateTimeOffset now)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        AgeEligibilityPolicy.CalculateAge(dateOfBirth, DateOnly.FromDateTime(now.UtcDateTime));
        return new()
        {
            AccountId = accountId, DateOfBirth = dateOfBirth, Gender = GenderInputPolicy.Normalize(gender),
            CreatedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime()
        };
    }
}
