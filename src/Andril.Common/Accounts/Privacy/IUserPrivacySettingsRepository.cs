namespace Andril.Common.Accounts.Privacy;

public interface IUserPrivacySettingsRepository
{
    Task<UserPrivacySettings?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<UserPrivacySettings> UpsertAsync(UserPrivacySettings settings, CancellationToken cancellationToken = default);
}
