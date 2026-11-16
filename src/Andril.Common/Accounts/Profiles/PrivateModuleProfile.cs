using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

/// <summary>Private identity root. Existence is never evidence of profile completion.</summary>
public sealed class PrivateModuleProfile
{
    private PrivateModuleProfile() { }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? DisplayName { get; private set; }
    public string? CanonicalDisplayName { get; private set; }
    public string? Bio { get; private set; }
    public string Visibility { get; private set; } = "private";
    public void SetDraftFields(ProfileEdit edit)
    {
        var valid = DisplayNames.Validate(edit);
        DisplayName = valid.DisplayName;
        CanonicalDisplayName = DisplayNames.Canonical(valid.DisplayName);
        Bio = valid.Bio;
        Visibility = valid.Visibility;
    }

    public static PrivateModuleProfile Create(Guid accountId, string module, DateTimeOffset now)
    {
        if (accountId == Guid.Empty || !ModuleNames.IsValid(module)
            || now == DateTimeOffset.MinValue || now == DateTimeOffset.MaxValue) throw new ProfileValidationException();
        return new() { Id = Guid.NewGuid(), AccountId = accountId, Module = module, CreatedAtUtc = now.ToUniversalTime() };
    }
}
