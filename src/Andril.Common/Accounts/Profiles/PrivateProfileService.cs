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

    public async Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
    {
        var (accountId, validate) = await AuthorizeAsync(module, cancellationToken);
        return await repository.WithAuthorizedProfileAsync(accountId, module, validate,
            profile => action(ToDto(profile, accountId, module)), cancellationToken);
    }

    public async Task<T> WithSerializableLearningProfileAsync<T>(Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (accountId, validate) = await AuthorizeAsync(ModuleNames.Learning, cancellationToken);
        return await repository.WithSerializableLearningProfileAsync(accountId, validate,
            profile => action(ToDto(profile, accountId, ModuleNames.Learning)), cancellationToken);
    }

    private async Task<(Guid AccountId, Action<StarterModuleState> Validate)> AuthorizeAsync(string module, CancellationToken ct)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        if (!identity.IsConfirmedFor(subject)) throw new OnboardingDependencyException();
        if (!ModuleNames.IsValid(module)) throw new ProfileValidationException();
        var account = await accounts.FindBySubjectAsync(subject, ct) ?? throw new StarterModuleAccountMissingException();
        if (account.Id == Guid.Empty || account.SupabaseUserId != subject) throw new AccountStoreUnavailableException();
        void Validate(StarterModuleState state)
        {
            ct.ThrowIfCancellationRequested();
            if (!user.IsAuthenticated || user.UserId != subject || !identity.IsConfirmedFor(subject))
                throw new ProfileAccessDeniedException();
            ValidatePrerequisites(account.Id, module, state, catalog, clock);
        }
        return (account.Id, Validate);
    }

    internal static void ValidatePrerequisites(Guid accountId, string module, StarterModuleState state,
        IPolicyCatalogProvider catalog, TimeProvider clock)
    {
        if (state.Membership is not { } membership || membership.AccountId != accountId || membership.Module != module
            || (module == ModuleNames.Social && membership.SocialOptInAtUtc is null))
            throw new ProfileAccessDeniedException();
        if (state.Demographics is not { } demographics || demographics.AccountId != accountId)
            throw new ProfileAccessDeniedException();
        ModuleAgeEligibility age;
        try
        {
            age = AgeEligibilityPolicy.Evaluate(demographics.DateOfBirth, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        }
        catch (EligibilityValidationException) { throw new ProfileAccessDeniedException(); }
        if (!(module switch { ModuleNames.Learning => age.Learning, ModuleNames.Hobbies => age.Hobbies, _ => age.Social }))
            throw new ProfileAccessDeniedException();
        var current = catalog.GetCurrent();
        if (!state.Policies.Any(x => x.AccountId == accountId && x.Action == PolicyActions.TermsAgreed && x.Version == current.Terms.Version)
            || !state.Policies.Any(x => x.AccountId == accountId && x.Action == PolicyActions.PrivacyAcknowledged && x.Version == current.Privacy.Version))
            throw new ProfileAccessDeniedException();
    }

    private async Task<PrivateProfileDto> Execute(string module, bool create, CancellationToken ct, ProfileEdit? edit = null)
    {
        var (accountId, validate) = await AuthorizeAsync(module, ct);
        var profile = edit is not null ? await repository.UpdateAsync(accountId, module, edit, validate, ct)
            : create ? await repository.GetOrCreateAsync(accountId, module, validate, clock, ct)
            : await repository.ReadAsync(accountId, module, validate, ct);
        return ToDto(profile, accountId, module);
    }

    private static PrivateProfileDto ToDto(PrivateModuleProfile? profile, Guid accountId, string module)
    {
        if (profile is null) throw new ProfileMissingException();
        if (profile.AccountId != accountId || profile.Module != module || profile.Id == Guid.Empty)
            throw new ProfileAccessDeniedException();
        return new(profile.Id, profile.Module, profile.CreatedAtUtc) { DisplayName = profile.DisplayName, Bio = profile.Bio, Visibility = profile.Visibility };
    }
}
