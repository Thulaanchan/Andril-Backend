using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

public sealed class PrivateProfileService(ICurrentUser user, IOnboardingIdentity identity,
    IAccountRepository accounts, IPrivateProfileRepository repository, IPolicyCatalogProvider catalog,
    TimeProvider clock) : IPrivateProfileService
{
    public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
        Execute(module, false, cancellationToken);
    public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
        Execute(module, true, cancellationToken);

    public Task<PrivateProfileDto> UpdateAsync(string module, ProfileEdit edit, CancellationToken cancellationToken = default) =>
        Execute(module, false, cancellationToken, DisplayNames.Validate(edit));

    private async Task<PrivateProfileDto> Execute(string module, bool create, CancellationToken ct, ProfileEdit? edit = null)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        if (!identity.IsConfirmedFor(subject)) throw new OnboardingDependencyException();
        if (!ModuleNames.IsValid(module)) throw new ProfileValidationException();
        var account = await accounts.FindBySubjectAsync(subject, ct) ?? throw new StarterModuleAccountMissingException();
        void Validate(StarterModuleState state)
        {
            if (state.Membership is not { } membership || membership.AccountId != account.Id || membership.Module != module
                || (module == ModuleNames.Social && membership.SocialOptInAtUtc is null))
                throw new ProfileAccessDeniedException();
            if (state.Demographics is not { } demographics || demographics.AccountId != account.Id)
                throw new ProfileAccessDeniedException();
            ModuleAgeEligibility age;
            try
            {
                GenderInputPolicy.Normalize(demographics.Gender);
                age = AgeEligibilityPolicy.Evaluate(demographics.DateOfBirth, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
            }
            catch (EligibilityValidationException) { throw new ProfileAccessDeniedException(); }
            if (!(module switch { ModuleNames.Learning => age.Learning, ModuleNames.Hobbies => age.Hobbies, _ => age.Social }))
                throw new ProfileAccessDeniedException();
            var current = catalog.GetCurrent();
            if (!state.Policies.Any(x => x.AccountId == account.Id && x.Action == PolicyActions.TermsAgreed && x.Version == current.Terms.Version)
                || !state.Policies.Any(x => x.AccountId == account.Id && x.Action == PolicyActions.PrivacyAcknowledged && x.Version == current.Privacy.Version))
                throw new ProfileAccessDeniedException();
        }
        var profile = edit is not null ? await repository.UpdateAsync(account.Id, module, edit, Validate, ct)
            : create ? await repository.GetOrCreateAsync(account.Id, module, Validate, clock, ct)
            : await repository.ReadAsync(account.Id, module, Validate, ct);
        if (profile is null) throw new ProfileMissingException();
        if (profile.AccountId != account.Id || profile.Module != module || profile.Id == Guid.Empty)
            throw new ProfileAccessDeniedException();
        return new(profile.Id, profile.Module, profile.CreatedAtUtc) { DisplayName = profile.DisplayName, Bio = profile.Bio, Visibility = profile.Visibility };
    }
}
