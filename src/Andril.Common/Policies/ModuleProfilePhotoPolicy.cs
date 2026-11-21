using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Policies;

public static class ModuleProfilePhotoPolicy
{
    public static bool RequiresStoredPhoto(string module)
    {
        if (!ModuleNames.IsValid(module)) throw new ProfileValidationException();
        return module == ModuleNames.Social;
    }

    // Supply only owner-scoped evidence from trusted persistence. This policy grants no module access.
    public static bool MeetsPhotoRequirement(PhotoScope scope, StoredProfilePhoto? photo)
    {
        if (!RequiresStoredPhoto(scope.Module)) return true;
        return photo is { State: PhotoState.Stored, IsCurrent: true }
            && photo.Address.Scope == scope && photo.Address.IsValid;
    }
}
