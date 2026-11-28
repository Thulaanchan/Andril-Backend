using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

public interface IModuleWelcomeService
{
    Task<ModuleWelcomeStatusDto> GetWelcomeStatusAsync(string module, CancellationToken cancellationToken = default);
    Task<ModuleWelcomeGetStartedResponse> GetStartedAsync(string module, ModuleWelcomeGetStartedRequest? request, CancellationToken cancellationToken = default);
}

public sealed class ModuleWelcomeService(
    ICurrentUser user,
    IOnboardingIdentity identity,
    IAccountRepository accounts,
    IStarterModuleService starter,
    IPrivateProfileRepository profileRepository,
    TimeProvider clock)
    : IModuleWelcomeService
{
    public async Task<ModuleWelcomeStatusDto> GetWelcomeStatusAsync(string module, CancellationToken cancellationToken = default)
    {
        var (account, state) = await AuthorizeCallerAndGetStateAsync(module, cancellationToken);
        var normModule = module.Trim().ToLowerInvariant();

        var moduleState = state.Modules.FirstOrDefault(m => string.Equals(m.Module, normModule, StringComparison.OrdinalIgnoreCase));
        bool isAgeEligible = moduleState?.AgeEligible ?? false;

        bool hasOptIn = state.Membership?.SocialOptIn ?? false;
        bool requiresSocialOptIn = normModule == ModuleNames.Social;

        // Check if user already has an existing profile
        PrivateModuleProfile? existingProfile = null;
        try
        {
            existingProfile = await profileRepository.ReadAsync(account.Id, normModule, _ => { }, cancellationToken);
        }
        catch (Exception)
        {
            // Ignore access errors during read check
        }

        bool hasProfile = existingProfile is not null;
        bool isComplete = hasProfile && !string.IsNullOrWhiteSpace(existingProfile!.DisplayName);

        string title = normModule switch
        {
            ModuleNames.Learning => "Start Your Learning Journey",
            ModuleNames.Hobbies => "Explore Your Hobbies",
            ModuleNames.Social => "Find Your People",
            _ => "Welcome"
        };

        string description = normModule switch
        {
            ModuleNames.Learning => "Discover personalized learning paths, track progress, and build your knowledge.",
            ModuleNames.Hobbies => "Connect with people who share your creative passions and explore local activities.",
            ModuleNames.Social => "Build genuine social connections, discover events, and meet like-minded peers.",
            _ => "Explore your module experience."
        };

        string? ineligibilityReason = null;
        if (!isAgeEligible)
        {
            ineligibilityReason = normModule == ModuleNames.Social
                ? "The Social module is strictly restricted to accounts aged 18 and older."
                : "You do not meet the age eligibility requirement for this module.";
        }
        else if (requiresSocialOptIn && !hasOptIn)
        {
            ineligibilityReason = "Explicit opt-in is required to access the Social module.";
        }

        bool canProceed = isAgeEligible && (!requiresSocialOptIn || hasOptIn);
        string stage = isComplete ? "preferences_pending" : hasProfile ? "profile_draft" : "profile_setup_required";

        return new ModuleWelcomeStatusDto(
            normModule,
            title,
            description,
            isAgeEligible && (!requiresSocialOptIn || hasOptIn),
            ineligibilityReason,
            hasProfile,
            stage,
            requiresSocialOptIn,
            hasOptIn,
            canProceed
        );
    }

    public async Task<ModuleWelcomeGetStartedResponse> GetStartedAsync(
        string module,
        ModuleWelcomeGetStartedRequest? request,
        CancellationToken cancellationToken = default)
    {
        var (account, state) = await AuthorizeCallerAndGetStateAsync(module, cancellationToken);
        var normModule = module.Trim().ToLowerInvariant();

        var moduleState = state.Modules.FirstOrDefault(m => string.Equals(m.Module, normModule, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProfileValidationException();

        if (!moduleState.AgeEligible)
        {
            throw new ModuleWelcomeIneligibleException(
                normModule == ModuleNames.Social
                    ? "The Social module is strictly restricted to users aged 18 and older."
                    : "Account does not meet the age eligibility requirement for this module.");
        }

        // Handle Social Opt-in
        if (normModule == ModuleNames.Social)
        {
            bool optInConfirmed = (request?.SocialOptIn == true) || (state.Membership?.SocialOptIn == true);
            if (!optInConfirmed)
            {
                throw new SocialOptInRequiredException("Explicit opt-in is required before entering the Social module.");
            }

            // Ensure starter module selection records the opt-in if not already recorded
            if (state.Membership is null || !state.Membership.SocialOptIn)
            {
                await starter.SelectAsync(ModuleNames.Social, true, cancellationToken);
            }
        }
        else if (state.Membership is null)
        {
            // Set as starter module if none selected yet
            await starter.SelectAsync(normModule, null, cancellationToken);
        }

        // Check or get existing profile
        PrivateModuleProfile? profile = null;
        try
        {
            profile = await profileRepository.ReadAsync(account.Id, normModule, _ => { }, cancellationToken);
        }
        catch (Exception)
        {
            // No profile yet
        }

        if (profile is null)
        {
            // Get or create draft shell
            profile = await profileRepository.GetOrCreateAsync(account.Id, normModule, _ => { }, clock, cancellationToken);
        }

        bool isProfileComplete = !string.IsNullOrWhiteSpace(profile.DisplayName);
        string nextStep = isProfileComplete ? "preferences_handoff" : "profile_setup";
        string stage = isProfileComplete ? "profile_created" : "profile_setup_required";

        return new ModuleWelcomeGetStartedResponse(
            normModule,
            true,
            nextStep,
            stage,
            isProfileComplete ? "Profile already initialized. Proceed to preferences." : "Module entry authorized. Proceed to profile setup."
        );
    }

    private async Task<(Account Account, StarterModuleStatus State)> AuthorizeCallerAndGetStateAsync(
        string module, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        if (!identity.IsConfirmedFor(subject))
            throw new OnboardingDependencyException();

        if (!ModuleNames.IsValid(module))
            throw new ProfileValidationException();

        var account = await accounts.FindBySubjectAsync(subject, cancellationToken)
            ?? throw new StarterModuleAccountMissingException();

        var state = await starter.GetAsync(cancellationToken);
        if (!state.DemographicsComplete)
            throw new ModuleWelcomeIneligibleException("Account demographics must be completed before accessing module welcome.");

        if (!state.RequiredPoliciesComplete)
            throw new ModuleWelcomeIneligibleException("Terms and privacy policies must be accepted before accessing module welcome.");

        return (account, state);
    }
}
