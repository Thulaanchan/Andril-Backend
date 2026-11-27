using Andril.Common.Accounts;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.Validators;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

public interface IDisplayNameAvailabilityService
{
    Task<DisplayNameAvailabilityResponse> CheckAvailabilityAsync(string module, string? name, CancellationToken cancellationToken = default);
}

public sealed class DisplayNameAvailabilityService(
    ICurrentUser user,
    IAccountRepository accounts,
    IDisplayNameAvailabilityRepository availabilityRepo)
    : IDisplayNameAvailabilityService
{
    public async Task<DisplayNameAvailabilityResponse> CheckAvailabilityAsync(string module, string? name, CancellationToken cancellationToken = default)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        if (!ModuleNames.IsValid(module))
            throw new ProfileValidationException();

        var account = await accounts.FindBySubjectAsync(subject, cancellationToken)
            ?? throw new StarterModuleAccountMissingException();

        if (string.IsNullOrWhiteSpace(name))
            return new(false, null, "Display name cannot be empty.");

        if (!PrivateProfileFieldValidator.TryNormalizeDisplayName(name, out var normalized))
            return new(false, null, "Display name must be 3-30 characters, use allowed characters, and not be a reserved name.");

        var canonical = DisplayNames.Canonical(normalized);
        var normModule = module.Trim().ToLowerInvariant();

        // 1. Same-module uniqueness: Another account in this module already uses this name
        var takenInModule = await availabilityRepo.IsDisplayNameTakenInModuleAsync(
            normModule, canonical, account.Id, cancellationToken);

        if (takenInModule)
            return new(false, null, "This display name is already taken in this module.");

        // 2. Same-account cross-module uniqueness: This account already uses this name in another module
        var usedBySameAccount = await availabilityRepo.IsDisplayNameUsedByAccountAsync(
            account.Id, canonical, normModule, cancellationToken);

        if (usedBySameAccount)
            return new(false, null, "You cannot reuse the same display name across your different module identities.");

        return new(true, normalized, null);
    }
}
