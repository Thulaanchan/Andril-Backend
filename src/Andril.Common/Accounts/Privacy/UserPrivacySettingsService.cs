using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Privacy;

public sealed class UserPrivacySettingsService(
    ICurrentUser currentUser,
    IAccountRepository accountRepository,
    IUserPrivacySettingsRepository privacyRepository,
    TimeProvider clock)
    : IUserPrivacySettingsService
{
    public async Task<UserPrivacySettingsDto> GetMyPrivacySettingsAsync(CancellationToken cancellationToken = default)
    {
        var account = await ResolveAccountAsync(cancellationToken);
        var settings = await privacyRepository.GetByAccountIdAsync(account.Id, cancellationToken);
        if (settings is null)
        {
            var defaultSettings = UserPrivacySettings.CreateDefault(account.Id, clock.GetUtcNow());
            settings = await privacyRepository.UpsertAsync(defaultSettings, cancellationToken);
        }

        return ToDto(settings);
    }

    public async Task<UserPrivacySettingsDto> UpdateMyPrivacySettingsAsync(UpdatePrivacySettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = await ResolveAccountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var existing = await privacyRepository.GetByAccountIdAsync(account.Id, cancellationToken);

        if (existing is null)
        {
            existing = UserPrivacySettings.CreateDefault(account.Id, now);
        }

        existing.Update(
            request.ProfileDiscoverability,
            request.GhostModeEnabled,
            request.DirectMessagingPolicy,
            request.AllowFriendRequests,
            request.ShowActiveModule,
            now);

        var updated = await privacyRepository.UpsertAsync(existing, cancellationToken);
        return ToDto(updated);
    }

    private async Task<Account> ResolveAccountAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        var account = await accountRepository.FindBySubjectAsync(subject, cancellationToken);
        if (account is null)
            throw new PrivacyNotFoundException("Account not initialized.");

        return account;
    }

    private static UserPrivacySettingsDto ToDto(UserPrivacySettings s) =>
        new(
            s.AccountId,
            s.ProfileDiscoverability,
            s.GhostModeEnabled,
            s.DirectMessagingPolicy,
            s.AllowFriendRequests,
            s.ShowActiveModule,
            s.CreatedAtUtc,
            s.UpdatedAtUtc
        );
}
