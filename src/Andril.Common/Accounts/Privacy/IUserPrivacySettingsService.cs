namespace Andril.Common.Accounts.Privacy;

public interface IUserPrivacySettingsService
{
    Task<UserPrivacySettingsDto> GetMyPrivacySettingsAsync(CancellationToken cancellationToken = default);
    Task<UserPrivacySettingsDto> UpdateMyPrivacySettingsAsync(UpdatePrivacySettingsRequest request, CancellationToken cancellationToken = default);
}
