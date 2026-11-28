using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.Validators;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

public interface IProfileSetupService
{
    Task<ProfileSetupStatusDto> GetSetupStatusAsync(string module, CancellationToken cancellationToken = default);
    Task<ProfileSetupSubmissionResponse> SubmitProfileAsync(string module, ProfileSetupSubmissionRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProfileSetupService(
    ICurrentUser user,
    IOnboardingIdentity identity,
    IAccountRepository accounts,
    IStarterModuleService starter,
    IPrivateProfileRepository profileRepository,
    IPrivateProfilePhotoStore photoStore,
    IAdditionalProfilePhotoRepository additionalPhotosRepository,
    IProfileLocationRepository locationRepository,
    TimeProvider clock,
    IPolicyCatalogProvider catalog)
    : IProfileSetupService
{
    public async Task<ProfileSetupStatusDto> GetSetupStatusAsync(string module, CancellationToken cancellationToken = default)
    {
        var (account, normModule, validate) = await AuthorizeCallerAsync(module, cancellationToken);

        var profile = await profileRepository.ReadAsync(account.Id, normModule, validate, cancellationToken);
        ValidateProfile(profile, account.Id, normModule);

        bool hasProfile = profile is not null;
        bool mainPhotoRequired = normModule == ModuleNames.Social;

        bool hasMainPhoto = false;
        if (hasProfile)
        {
            var currentPhoto = await photoStore.ReadCurrentAsync(new(profile!.Id, normModule), cancellationToken);
            hasMainPhoto = currentPhoto is not null;
        }

        int additionalPhotoCount = 0;
        if (hasProfile)
        {
            var addPhotos = await additionalPhotosRepository.GetByProfileIdAsync(profile!.Id, cancellationToken);
            if (addPhotos.Any(p => p.ProfileId != profile.Id || p.AccountId != account.Id || p.Module != normModule))
                throw new ProfileAccessDeniedException();
            additionalPhotoCount = addPhotos.Count;
        }

        ProfileLocationRecord? location = null;
        if (hasProfile)
        {
            location = await locationRepository.GetByProfileIdAsync(profile!.Id, cancellationToken);
            ValidateLocation(location, profile);
        }

        bool isDisplayNameSet = hasProfile && !string.IsNullOrWhiteSpace(profile!.DisplayName);
        bool isPhotoSatisfied = !mainPhotoRequired || hasMainPhoto;
        bool isReadyForPreferences = isDisplayNameSet && isPhotoSatisfied;

        string stage = isReadyForPreferences ? "completed" : isDisplayNameSet ? "photo_pending" : "draft";

        string handoffRoute = normModule switch
        {
            ModuleNames.Learning => "/learning/preferences",
            ModuleNames.Hobbies => "/hobbies/preferences",
            ModuleNames.Social => "/social/preferences",
            _ => "/preferences"
        };

        return new ProfileSetupStatusDto(
            normModule,
            hasProfile,
            profile?.Id,
            profile?.DisplayName,
            hasMainPhoto,
            mainPhotoRequired,
            additionalPhotoCount,
            location is not null,
            location?.City,
            stage,
            isReadyForPreferences,
            handoffRoute
        );
    }

    public async Task<ProfileSetupSubmissionResponse> SubmitProfileAsync(
        string module, ProfileSetupSubmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (account, normModule, validate) = await AuthorizeCallerAsync(module, cancellationToken);

        // 1. Validate Display Name
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ProfileValidationException();

        if (!PrivateProfileFieldValidator.TryNormalizeDisplayName(request.DisplayName, out var normalizedDisplayName))
            throw new ProfileValidationException();

        // 2. Validate About Me (Max 100 chars per UI spec)
        if (!string.IsNullOrEmpty(request.Bio))
        {
            if (request.Bio.Length > 100)
                throw new ProfileBioTooLongException($"About Me exceeds the 100 character limit. Current length: {request.Bio.Length}");
            if (!PrivateProfileFieldValidator.IsValidBio(request.Bio))
                throw new ProfileValidationException();
        }

        // Reject invalid optional fields before any relational write.
        if (!string.IsNullOrWhiteSpace(request.LocationCity)
            && !ApprovedLocationsCatalog.IsValidLocation(request.LocationCity))
            throw new ProfileValidationException();
        if (request.LocationCity?.Trim().Length > 100 || request.LocationRegion?.Trim().Length > 100
            || (string.IsNullOrWhiteSpace(request.LocationCity) && !string.IsNullOrWhiteSpace(request.LocationRegion)))
            throw new ProfileValidationException();

        var edit = new ProfileEdit(normalizedDisplayName, request.Bio, "private");
        return await profileRepository.WithProfileSetupAsync(account.Id, normModule, edit, validate, clock, async profile =>
        {
            ValidateProfile(profile, account.Id, normModule);
            // 4. Validate Social Main Photo requirement
            var currentPhoto = await photoStore.ReadCurrentAsync(new(profile.Id, normModule), cancellationToken);
            bool hasMainPhoto = currentPhoto is not null;

            if (normModule == ModuleNames.Social && !hasMainPhoto)
            {
                throw new SocialProfileRequiresMainPhotoException("A main profile photo is required to complete your Social profile.");
            }

            // 6. Handle Location if provided
            var now = clock.GetUtcNow();
            var committedLocation = await locationRepository.GetByProfileIdAsync(profile.Id, cancellationToken);
            ValidateLocation(committedLocation, profile);
            if (!string.IsNullOrWhiteSpace(request.LocationCity))
            {
                if (!ApprovedLocationsCatalog.IsValidLocation(request.LocationCity))
                    throw new ProfileValidationException();

                var existingLocation = committedLocation;
                if (existingLocation is null)
                {
                    var newLocation = ProfileLocationRecord.Create(
                        profile.Id, account.Id, normModule, request.LocationCity, request.LocationRegion, now);
                    committedLocation = await locationRepository.UpsertAsync(newLocation, cancellationToken);
                }
                else
                {
                    existingLocation.Update(request.LocationCity, request.LocationRegion, now);
                    committedLocation = await locationRepository.UpsertAsync(existingLocation, cancellationToken);
                }
            }

            ValidateLocation(committedLocation, profile);

            // 7. Count additional photos
            var addPhotos = await additionalPhotosRepository.GetByProfileIdAsync(profile.Id, cancellationToken);

            if (addPhotos.Any(p => p.ProfileId != profile.Id || p.AccountId != account.Id || p.Module != normModule))
                throw new ProfileAccessDeniedException();

            // 8. Construct Preferences Handoff
            var (ownerMember, handoffRoute) = normModule switch
            {
                ModuleNames.Learning => ("M1 - Thusa", "/learning/preferences"),
                ModuleNames.Hobbies => ("M3 - Priyanka", "/hobbies/preferences"),
                ModuleNames.Social => ("M5 - Thulaanchan", "/social/preferences"),
                _ => ("Downstream Member", "/preferences")
            };

            var handoff = new PreferencesHandoffDto(
                normModule,
                ownerMember,
                handoffRoute,
                true,
                false,
                "M2 profile creation complete. Proceed to module-specific preferences owned by downstream member."
            );

            return new ProfileSetupSubmissionResponse(
                profile.Id,
                normModule,
                profile.DisplayName!,
                profile.Bio,
                committedLocation?.City,
                committedLocation?.Region,
                hasMainPhoto,
                addPhotos.Count,
                "completed",
                handoffRoute,
                handoff
            );
        }, cancellationToken);
    }

    private static void ValidateProfile(PrivateModuleProfile? profile, Guid accountId, string module)
    {
        if (profile is not null && (profile.Id == Guid.Empty || profile.AccountId != accountId || profile.Module != module))
            throw new ProfileAccessDeniedException();
    }

    private static void ValidateLocation(ProfileLocationRecord? location, PrivateModuleProfile profile)
    {
        if (location is not null && (location.ProfileId != profile.Id || location.AccountId != profile.AccountId || location.Module != profile.Module))
            throw new ProfileAccessDeniedException();
    }

    private async Task<(Account Account, string NormModule, Action<StarterModuleState> Validate)> AuthorizeCallerAsync(
        string module, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        if (!identity.IsConfirmedFor(subject))
            throw new OnboardingDependencyException();

        if (!ModuleNames.IsValid(module))
            throw new ProfileValidationException();

        var normModule = module.Trim().ToLowerInvariant();
        var account = await accounts.FindBySubjectAsync(subject, cancellationToken)
            ?? throw new StarterModuleAccountMissingException();

        var state = await starter.GetAsync(cancellationToken);
        if (!state.DemographicsComplete)
            throw new ProfileAccessDeniedException();

        if (!state.RequiredPoliciesComplete)
            throw new ProfileAccessDeniedException();

        var moduleState = state.Modules.FirstOrDefault(m => string.Equals(m.Module, normModule, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProfileValidationException();

        if (!moduleState.AgeEligible)
            throw new ProfileAccessDeniedException();

        if (normModule == ModuleNames.Social && (state.Membership is null || !state.Membership.SocialOptIn))
            throw new ProfileAccessDeniedException();

        if (account.Id == Guid.Empty || account.SupabaseUserId != subject) throw new AccountStoreUnavailableException();
        void Validate(StarterModuleState current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!user.IsAuthenticated || user.UserId != subject || !identity.IsConfirmedFor(subject))
                throw new ProfileAccessDeniedException();
            PrivateProfileService.ValidatePrerequisites(account.Id, normModule, current, catalog, clock);
        }
        return (account, normModule, Validate);
    }
}
