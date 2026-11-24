using Andril.Common.Accounts.StarterModules;
using Andril.Common.Validators;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

public sealed record PrivateProfileDto(Guid ProfileId, string Module, DateTimeOffset CreatedAtUtc)
{
    public string? DisplayName { get; init; }
    public string? Bio { get; init; }
    public string Visibility { get; init; } = "private";
    public bool IsComplete => false;
    public string BlockingDependency => "approved_profile_contract";
}

public interface IPrivateProfileRepository
{
    Task<T> WithAuthorizedProfileAsync<T>(Guid accountId, string module, Action<StarterModuleState> validate,
        Func<PrivateModuleProfile, Task<T>> action, CancellationToken cancellationToken) => throw new PhotoDependencyException();

    // Reads and creation validate authoritative prerequisites inside the same transaction.
    Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module,
        Action<StarterModuleState> validate, CancellationToken cancellationToken = default);
    Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module,
        Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default);
    Task<PrivateModuleProfile> UpdateAsync(Guid accountId, string module, ProfileEdit edit,
        Action<StarterModuleState> validate, CancellationToken cancellationToken = default) => throw new ProfileContractPendingException();
}

public interface IPrivateProfileService
{
    Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action,
        CancellationToken cancellationToken) => throw new PhotoDependencyException();
    Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default);
    Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default);
    Task<PrivateProfileDto> UpdateAsync(string module, ProfileEdit edit, CancellationToken cancellationToken = default) => throw new ProfileContractPendingException();
}

public sealed record ProfileEdit(string DisplayName, string? Bio, string Visibility);
public static class DisplayNames
{
    public static string Trim(string value) => string.IsNullOrWhiteSpace(value) ? throw new ProfileValidationException() : value.Trim();
    public static string Canonical(string value) => string.Concat(Trim(value).Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
    public static ProfileEdit Validate(ProfileEdit edit)
    {
        if (edit.Visibility != "private"
            || !PrivateProfileFieldValidator.TryNormalizeDisplayName(edit.DisplayName, out var name)
            || !PrivateProfileFieldValidator.IsValidBio(edit.Bio))
            throw new ProfileValidationException();
        return edit with { DisplayName = name };
    }
}
public sealed class ProfileNameConflictException : Exception { }

// Server-derived private-profile evidence only; this result never authorizes module access.
public sealed record ProfileCompletionStatus(string? BlockingDependency, bool IsComplete = false);
public interface IPrivateProfileCompletionReader
{
    Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default);
}

public sealed class PrivateProfileCompletionReader(IPrivateProfileService profiles) : IPrivateProfileCompletionReader
{
    public async Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default)
    {
        PrivateProfileDto profile;
        // The existing owner-scoped read rechecks membership, opt-in, age and current policies
        // within the profile repository transaction. Do not replace it with a raw profile lookup.
        try { profile = await profiles.GetAsync(module, cancellationToken); }
        catch (ProfileMissingException) { return new("private_profile_missing"); }
        if (module == ModuleNames.Social)
        {
            if (profile.Module != module || profile.ProfileId == Guid.Empty) throw new ProfileAccessDeniedException();
            // AND-M2-SOC-ONB-DEC-20261121-01: optional fields do not gate completion.
            return PrivateProfileFieldValidator.TryNormalizeDisplayName(profile.DisplayName, out var normalized)
                && normalized == profile.DisplayName
                ? new(null, true) : new("social_display_name");
        }
        return new("approved_profile_contract");
    }
}

/// <summary>Module handlers must invoke this at their own protected boundary, not trust Flutter routing.</summary>
public interface IPrivateModuleAccess
{
    Task RequireAsync(string module, CancellationToken cancellationToken = default);
}
public sealed class PrivateModuleAccess(IPrivateProfileCompletionReader completion) : IPrivateModuleAccess
{
    public async Task RequireAsync(string module, CancellationToken cancellationToken = default)
    {
        await completion.ReadAsync(module, cancellationToken);
        // Social completion can be derived from approved persisted prerequisites.
        // Learning and Hobbies still require authoritative module-owned evidence.
        // Protected Home entry requires a separate authorization contract.
        throw new ProfileContractPendingException();
    }
}

public sealed class ProfileValidationException : Exception { }
public sealed class ProfileMissingException : Exception { }
public sealed class ProfileAccessDeniedException : Exception { }
public sealed class ProfileContractPendingException : Exception { }
