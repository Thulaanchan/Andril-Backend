using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Preferences;

public sealed class LanguagePreferenceService(ICurrentUser currentUser, IAccountRepository accounts,
    ILanguagePreferenceRepository preferences, TimeProvider clock) : ILanguagePreferenceService
{
    public async Task<LanguagePreferenceDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var account = await ResolveAccountAsync(cancellationToken);
        var preference = await preferences.FindAsync(account.Id, cancellationToken);
        return preference is null ? new("en", false) : new(preference.Language, true);
    }

    public async Task<LanguagePreferenceDto> UpdateAsync(string? language, string? mode, CancellationToken cancellationToken = default)
    {
        RequireSubject();
        if (!AccountLanguagePreference.IsSupported(language) || mode is not ("sync" or "settings"))
            throw new LanguagePreferenceValidationException();
        var account = await ResolveAccountAsync(cancellationToken);
        var preference = await preferences.WriteAsync(account.Id, language!, mode == "sync", clock.GetUtcNow(), cancellationToken);
        return new(preference.Language, true);
    }

    private Guid RequireSubject()
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return subject;
    }

    private async Task<Account> ResolveAccountAsync(CancellationToken cancellationToken) =>
        await accounts.FindBySubjectAsync(RequireSubject(), cancellationToken)
        ?? throw new LanguagePreferenceAccountMissingException();
}
