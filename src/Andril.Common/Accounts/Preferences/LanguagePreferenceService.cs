using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Preferences;

public sealed class LanguagePreferenceService(ICurrentUser currentUser, IAccountService accounts,
    ILanguagePreferenceRepository preferences, TimeProvider clock) : ILanguagePreferenceService
{
    public async Task<LanguagePreferenceDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var (subject, accountId) = await ResolveAccountAsync(cancellationToken);
        var preference = await preferences.FindAsync(accountId, cancellationToken);
        RequireUnchangedSubject(subject, cancellationToken);
        ValidatePreference(preference, accountId);
        return preference is null ? new("en", false) : new(preference.Language, true);
    }

    public async Task<LanguagePreferenceDto> UpdateAsync(string? language, string? mode, CancellationToken cancellationToken = default)
    {
        RequireSubject(cancellationToken);
        if (!AccountLanguagePreference.IsSupported(language) || mode is not ("sync" or "settings"))
            throw new LanguagePreferenceValidationException();
        var (subject, accountId) = await ResolveAccountAsync(cancellationToken);
        var preference = await preferences.WriteAsync(accountId, language!, mode == "sync", clock.GetUtcNow(), cancellationToken);
        RequireUnchangedSubject(subject, cancellationToken);
        ValidatePreference(preference, accountId);
        return preference is null ? throw new AccountStoreUnavailableException() : new(preference.Language, true);
    }

    private Guid RequireSubject(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return subject;
    }

    private async Task<(Guid Subject, Guid AccountId)> ResolveAccountAsync(CancellationToken ct)
    {
        var subject = RequireSubject(ct);
        var account = await accounts.GetMeAsync(ct) ?? throw new LanguagePreferenceAccountMissingException();
        RequireUnchangedSubject(subject, ct);
        if (account.AccountId == Guid.Empty) throw new AccountStoreUnavailableException();
        return (subject, account.AccountId);
    }

    private void RequireUnchangedSubject(Guid subject, CancellationToken ct)
    {
        if (RequireSubject(ct) != subject) throw new AccountAuthenticationRequiredException();
    }

    private static void ValidatePreference(AccountLanguagePreference? preference, Guid accountId)
    {
        if (preference is not null && (preference.AccountId != accountId || !AccountLanguagePreference.IsSupported(preference.Language)))
            throw new AccountStoreUnavailableException();
    }
}
