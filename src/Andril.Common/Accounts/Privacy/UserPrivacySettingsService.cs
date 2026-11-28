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
        var settings = await privacyRepository.GetOrCreateAsync(account.Id, clock.GetUtcNow(), cancellationToken);
        if (settings.AccountId != account.Id) throw new AccountStoreUnavailableException();

        return ToDto(settings);
    }

    public async Task<UserPrivacySettingsDto> UpdateMyPrivacySettingsAsync(UpdatePrivacySettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var account = await ResolveAccountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var existing = await privacyRepository.GetByAccountIdAsync(account.Id, cancellationToken);
        if (existing is not null && existing.AccountId != account.Id) throw new AccountStoreUnavailableException();

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
        if (updated.AccountId != account.Id) throw new AccountStoreUnavailableException();
        return ToDto(updated);
    }

    private async Task<Account> ResolveAccountAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        var account = await accountRepository.FindBySubjectAsync(subject, cancellationToken);
        if (account is null)
            throw new PrivacyNotFoundException("Account not initialized.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId != subject)
            throw new AccountAuthenticationRequiredException();
        if (account.Id == Guid.Empty || account.SupabaseUserId != subject)
            throw new AccountStoreUnavailableException();

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
