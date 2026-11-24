using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Onboarding;

public sealed class OnboardingStatusService(ICurrentUser user, IOnboardingIdentity identity,
    IAccountRepository accounts, IStarterModuleService starter,
    IPrivateProfileCompletionReader? profiles = null) : IOnboardingStatusService
{
    public Task<OnboardingStatus> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync(profiles, null, cancellationToken);

    internal Task<OnboardingStatus> GetControlledAsync(ModuleNavigationService navigation,
        IPrivateProfileCompletionReader controlledProfiles, CancellationToken cancellationToken = default) =>
        GetAsync(controlledProfiles, navigation, cancellationToken);

    private async Task<OnboardingStatus> GetAsync(IPrivateProfileCompletionReader? profileReader,
        ModuleNavigationService? navigation, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        if (!identity.IsConfirmedFor(subject)) throw new OnboardingDependencyException();
        if (await accounts.FindBySubjectAsync(subject, cancellationToken) is null)
            return new("initialize_account", new(true, false, false, false, false));

        StarterModuleStatus state;
        try { state = await starter.GetAsync(cancellationToken); }
        // The account can be deleted between reads. Do not report a partially observed completion.
        catch (StarterModuleAccountMissingException) { throw new OnboardingDependencyException(); }

        var membership = state.Membership;
        if (membership is not null && (!ModuleNames.IsValid(membership.Module)
            || (membership.Module == ModuleNames.Social && !membership.SocialOptIn)))
            throw new OnboardingDependencyException();

        // Require the complete backend module result; absent dependencies never imply eligibility.
        if (state.Modules.Count != 3 || new[] { "learning", "hobbies", "social" }
            .Any(module => state.Modules.Count(x => x.Module == module) != 1))
            throw new OnboardingDependencyException();

        var progress = new OnboardingProgress(true, true, state.DemographicsComplete,
            state.RequiredPoliciesComplete, membership is not null);
        string next;
        if (!state.DemographicsComplete) next = "complete_demographics";
        else if (!state.Modules.Any(x => x.AgeEligible)) next = "age_ineligible";
        else if (!state.RequiredPoliciesComplete) next = "review_policies";
        else if (membership is null) next = "select_starter";
        else if (!state.Modules.Single(x => x.Module == membership.Module).AgeEligible) next = "age_ineligible";
        else next = "profile_setup";

        if (next != "profile_setup")
            return new(next, progress, membership?.Module);

        var module = membership!.Module;
        if (navigation is not null)
        {
            var status = await navigation.ReadAsync(module, cancellationToken);
            if (!identity.IsConfirmedFor(subject))
                throw new OnboardingDependencyException();

            if (status.State != ModuleNavigationState.Active)
            {
                next = status.State switch
                {
                    ModuleNavigationState.NeverActivated => "activate_module",
                    ModuleNavigationState.Inactive => "reactivate_module",
                    _ => throw new OnboardingDependencyException()
                };
                return new(next, progress, module);
            }
        }
        if (profileReader is null)
            return new(next, progress, module, "private_profile_infrastructure");

        ProfileCompletionStatus completion;
        try
        {
            completion = await profileReader.ReadAsync(module, cancellationToken);
        }
        // A prerequisite may change between status and the authoritative profile read.
        catch (ProfileAccessDeniedException) { throw new OnboardingDependencyException(); }
        catch (StarterModuleAccountMissingException) { throw new OnboardingDependencyException(); }

        progress = progress with
        {
            PrivateProfileComplete = module == ModuleNames.Social && completion.IsComplete
                && completion.BlockingDependency is null
        };
        if (navigation is not null)
        {
            if (!user.IsAuthenticated || user.UserId != subject || !identity.IsConfirmedFor(subject))
                throw new OnboardingDependencyException();
            next = completion.BlockingDependency switch
            {
                "private_profile_missing" => "create_private_profile",
                null when progress.PrivateProfileComplete => "onboarding_complete",
                _ => "complete_module_setup"
            };
        }
        return new(next, progress, module, completion.BlockingDependency);
    }
}
