namespace Andril.Common.Accounts.StarterModules;

// Private onboarding membership, never a public identity or module profile.
public sealed class StarterMembership
{
    private StarterMembership() { }
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = "";
    public DateTimeOffset ActivatedAtUtc { get; private set; }
    public DateTimeOffset? SocialOptInAtUtc { get; private set; }

    public static StarterMembership Create(Guid accountId, string? module, bool? socialOptIn, DateTimeOffset now)
    {
        if (accountId == Guid.Empty || !StarterModules.IsValid(module)
            || (module == StarterModules.Social ? socialOptIn != true : socialOptIn == true)
            || now == DateTimeOffset.MinValue || now == DateTimeOffset.MaxValue)
            throw new StarterModuleValidationException();
        var utc = now.ToUniversalTime();
        return new() { AccountId = accountId, Module = module!, ActivatedAtUtc = utc,
            SocialOptInAtUtc = module == StarterModules.Social ? utc : null };
    }
}
