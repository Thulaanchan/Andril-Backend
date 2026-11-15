namespace Andril.Common.Accounts.Eligibility;

// All eligibility is computed, not persisted. Age eligibility is not activation authorization.
public sealed record AccountEligibilityDto(bool HasSubmission, DateOnly EvaluatedOnUtc, ModuleAgeEligibility AgeEligibility);

public interface IAccountDemographicsRepository
{
    Task<AccountDemographics?> FindAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountDemographics> SaveAsync(Guid accountId, DateOnly dateOfBirth, string gender, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IAccountEligibilityService
{
    Task<AccountEligibilityDto> GetAsync(CancellationToken cancellationToken = default);
    Task<AccountEligibilityDto> SubmitAsync(DateOnly? dateOfBirth, string? gender, CancellationToken cancellationToken = default);
}

public sealed class EligibilityValidationException : Exception
{
    public EligibilityValidationException() : base("Valid date of birth and nonempty gender are required.") { }
}

public sealed class EligibilityAccountMissingException : Exception
{
    public EligibilityAccountMissingException() : base("Account not initialized.") { }
}
