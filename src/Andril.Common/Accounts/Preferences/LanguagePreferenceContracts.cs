namespace Andril.Common.Accounts.Preferences;

/// <summary>IsStored distinguishes an account value from the non-persisted display fallback.</summary>
public sealed record LanguagePreferenceDto(string Language, bool IsStored);

public interface ILanguagePreferenceRepository
{
    Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}

public interface ILanguagePreferenceService
{
    Task<LanguagePreferenceDto> GetAsync(CancellationToken cancellationToken = default);
    Task<LanguagePreferenceDto> UpdateAsync(string? language, string? mode, CancellationToken cancellationToken = default);
}

public sealed class LanguagePreferenceValidationException : Exception
{
    public LanguagePreferenceValidationException() : base("Use language en, ta or si and mode sync or settings.") { }
}

public sealed class LanguagePreferenceAccountMissingException : Exception
{
    public LanguagePreferenceAccountMissingException() : base("Account not initialized.") { }
}
