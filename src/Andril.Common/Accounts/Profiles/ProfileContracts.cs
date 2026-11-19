using Andril.Common.Accounts.StarterModules;

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
        if (edit.Visibility != "private" || edit.DisplayName is null || edit.DisplayName.Contains('\0') || edit.Bio?.Contains('\0') == true)
            throw new ProfileValidationException();
        return edit with { DisplayName = Trim(edit.DisplayName) };
    }
}
public sealed class ProfileNameConflictException : Exception { }

// No injectable "isComplete" boolean or configurable bypass. An approved persisted field
// contract must be implemented before this boundary can ever authorize module access.
public sealed record ProfileCompletionStatus(string BlockingDependency)
{
    public bool IsComplete => false;
}
public interface IPrivateProfileCompletionReader
{
    Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default);
}

public sealed class PrivateProfileCompletionReader(IPrivateProfileService profiles) : IPrivateProfileCompletionReader
{
    public async Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default)
    {
        try { await profiles.GetAsync(module, cancellationToken); }
        catch (ProfileMissingException) { return new("private_profile_missing"); }
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
        // No approved completion/privacy contract exists, so no module home may be authorized.
        throw new ProfileContractPendingException();
    }
}

public sealed class ProfileValidationException : Exception { }
public sealed class ProfileMissingException : Exception { }
public sealed class ProfileAccessDeniedException : Exception { }
public sealed class ProfileContractPendingException : Exception { }
