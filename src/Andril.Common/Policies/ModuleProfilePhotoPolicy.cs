
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;

namespace Andril.Common.Policies;

/// <summary>
/// Defines module-specific profile-photo requirements.
///
/// Learning and Hobbies: photo is optional.
/// Social (Dating): an approved photo is mandatory.
///
/// This policy must only receive photo evidence retrieved
/// from trusted, owner-scoped server-side persistence.
/// It must never trust photo status submitted by a client.
/// </summary>
public static class ModuleProfilePhotoPolicy
{
    /// <summary>
    /// Determines whether a module requires an approved
    /// profile photo to complete onboarding.
    /// </summary>
    public static bool RequiresApprovedPhoto(string module)
    {
        ValidateModule(module);

        return module == StarterModules.Social;
    }

    /// <summary>
    /// Evaluates only the photo requirement.
    ///
    /// This does not determine overall profile completion
    /// or grant module-home authorization.
    /// </summary>
    public static bool MeetsPhotoRequirement(
        string module,
        PrivatePhotoStatus? photo)
    {
        ValidateModule(module);

        // Learning and Hobbies can complete their photo
        // requirement without an uploaded image.
        if (module != StarterModules.Social)
            return true;

        // Dating requires a valid, approved, server-verified
        // photo associated with the authorized module profile.
        if (photo is null)
            return false;

        if (photo.UploadId == Guid.Empty)
            return false;

        if (photo.State != PhotoState.Approved)
            return false;

        if (photo.Reason != PhotoReason.None)
            return false;

        return true;
    }

    private static void ValidateModule(string module)
    {
        if (string.IsNullOrWhiteSpace(module) ||
            !StarterModules.IsValid(module))
        {
            throw new ProfileValidationException();
        }
    }
}
