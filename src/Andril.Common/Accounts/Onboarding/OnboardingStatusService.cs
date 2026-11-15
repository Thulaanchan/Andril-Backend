using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Onboarding;

public sealed class OnboardingStatusService(ICurrentUser user, IOnboardingIdentity identity,
    IAccountRepository accounts, IStarterModuleService starter) : IOnboardingStatusService
{
    public async Task<OnboardingStatus> GetAsync(CancellationToken cancellationToken = default)
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

        // Membership, gallery ownership and questionnaire answers are not private-profile evidence.
        return new(next, progress, membership?.Module,
            next == "profile_setup" ? "private_profile_infrastructure" : null);
    }
}
