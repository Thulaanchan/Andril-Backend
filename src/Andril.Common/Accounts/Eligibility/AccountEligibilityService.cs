using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Eligibility;

public sealed class AccountEligibilityService(ICurrentUser user, IAccountRepository accounts,
    IAccountDemographicsRepository demographics, TimeProvider clock) : IAccountEligibilityService
{
    public async Task<AccountEligibilityDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var account = await ResolveAccount(cancellationToken);
        var record = await demographics.FindAsync(account.Id, cancellationToken);
        var date = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return ToDto(record, date);
    }

    public async Task<AccountEligibilityDto> SubmitAsync(DateOnly? dateOfBirth, string? gender, CancellationToken cancellationToken = default)
    {
        RequireSubject();
        var now = clock.GetUtcNow();
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        if (dateOfBirth is not DateOnly birth) throw new EligibilityValidationException();
        AgeEligibilityPolicy.CalculateAge(birth, date);
        var normalizedGender = GenderInputPolicy.Normalize(gender);
        var account = await ResolveAccount(cancellationToken);
        var saved = await demographics.SaveAsync(account.Id, birth, normalizedGender, now, cancellationToken);
        return ToDto(saved, date);
    }

    private static AccountEligibilityDto ToDto(AccountDemographics? record, DateOnly date) => record is null
        ? new(false, date, new(false, false, false))
        : new(true, date, AgeEligibilityPolicy.Evaluate(record.DateOfBirth, date));

    private Guid RequireSubject()
    {
        if (!user.IsAuthenticated || user.UserId is not Guid id || id == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return id;
    }

    private async Task<Account> ResolveAccount(CancellationToken cancellationToken) =>
        await accounts.FindBySubjectAsync(RequireSubject(), cancellationToken) ?? throw new EligibilityAccountMissingException();
}
