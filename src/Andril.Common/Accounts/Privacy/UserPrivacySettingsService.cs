using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Privacy;

public sealed class UserPrivacySettingsService(
    ICurrentUser currentUser,
    IAccountService accounts,
    IUserPrivacySettingsRepository privacyRepository,
    TimeProvider clock)
    : IUserPrivacySettingsService
{
    public async Task<UserPrivacySettingsDto> GetMyPrivacySettingsAsync(CancellationToken cancellationToken = default)
    {
        var (subject, accountId) = await ResolveAccountAsync(cancellationToken);
        var settings = await privacyRepository.GetOrCreateAsync(accountId, clock.GetUtcNow(), cancellationToken);
        RequireUnchangedSubject(subject, cancellationToken);
        if (settings is null || settings.AccountId != accountId) throw new AccountStoreUnavailableException();

        return ToDto(settings);
    }

    public async Task<UserPrivacySettingsDto> UpdateMyPrivacySettingsAsync(UpdatePrivacySettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (subject, accountId) = await ResolveAccountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var existing = await privacyRepository.GetByAccountIdAsync(accountId, cancellationToken);
        RequireUnchangedSubject(subject, cancellationToken);
        if (existing is not null && existing.AccountId != accountId) throw new AccountStoreUnavailableException();

        if (existing is null)
        {
            existing = UserPrivacySettings.CreateDefault(accountId, now);
        }

        existing.Update(
            request.ProfileDiscoverability,
            request.GhostModeEnabled,
            request.DirectMessagingPolicy,
            request.AllowFriendRequests,
            request.ShowActiveModule,
            now);

        var updated = await privacyRepository.UpsertAsync(existing, cancellationToken);
        RequireUnchangedSubject(subject, cancellationToken);
        if (updated is null || updated.AccountId != accountId) throw new AccountStoreUnavailableException();
        return ToDto(updated);
    }

    private Guid RequireSubject(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return subject;
    }

    private void RequireUnchangedSubject(Guid subject, CancellationToken ct)
    {
        if (RequireSubject(ct) != subject) throw new AccountAuthenticationRequiredException();
    }

    private async Task<(Guid Subject, Guid AccountId)> ResolveAccountAsync(CancellationToken ct)
    {
        var subject = RequireSubject(ct);
        var account = await accounts.GetMeAsync(ct) ?? throw new PrivacyNotFoundException("Account not initialized.");
        RequireUnchangedSubject(subject, ct);
        if (account.AccountId == Guid.Empty) throw new AccountStoreUnavailableException();
        return (subject, account.AccountId);
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
