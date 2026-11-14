namespace Andril.Common.Accounts.Preferences;

public sealed class AccountLanguagePreference
{
    private AccountLanguagePreference() { }
    public Guid AccountId { get; private set; }
    public string Language { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static bool IsSupported(string? language) => language is "en" or "ta" or "si";

    public static AccountLanguagePreference Create(Guid accountId, string language, DateTimeOffset now)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (!IsSupported(language)) throw new LanguagePreferenceValidationException();
        return new AccountLanguagePreference
        {
            AccountId = accountId, Language = language,
            CreatedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime()
        };
    }
}
