using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.StarterModules;

public sealed class StarterModuleService(ICurrentUser user, IAccountRepository accounts,
    IStarterModuleRepository repository, IPolicyCatalogProvider catalog, TimeProvider clock) : IStarterModuleService
{
    public async Task<StarterModuleStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        var current = catalog.GetCurrent();
        var account = await ResolveAccount(subject, cancellationToken);
        var state = await repository.ReadAsync(account.Id, cancellationToken);
        return Status(state, current, clock.GetUtcNow());
    }

    public async Task<StarterModuleStatus> SelectAsync(string? module, bool? socialOptIn, CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject();
        if (!StarterModules.IsValid(module) || (module == StarterModules.Social ? socialOptIn != true : socialOptIn == true))
            throw new StarterModuleValidationException();
        var current = catalog.GetCurrent();
        var account = await ResolveAccount(subject, cancellationToken);
        DateTimeOffset evaluatedAt = default;
        var state = await repository.SelectAsync(account.Id, locked =>
        {
            // Capture UTC after acquiring locks, including a wait that may cross a birthday.
            evaluatedAt = clock.GetUtcNow();
            if (locked.Membership is { } existing && existing.Module != module)
                throw new StarterModuleConflictException();
            var status = Status(locked, current, evaluatedAt);
            if (!status.DemographicsComplete || !status.RequiredPoliciesComplete)
                throw new StarterModulePrerequisitesException();
            if (!status.Modules.Single(x => x.Module == module).AgeEligible)
                throw new StarterModuleIneligibleException();
            return locked.Membership ?? StarterMembership.Create(account.Id, module, socialOptIn, evaluatedAt);
        }, cancellationToken);
        return Status(state, current, evaluatedAt);
    }

    private static StarterModuleStatus Status(StarterModuleState state, PolicyCatalog current, DateTimeOffset now)
    {
        var complete = false;
        var age = new ModuleAgeEligibility(false, false, false);
        if (state.Demographics is { } demographics)
        {
            try
            {
                GenderInputPolicy.Normalize(demographics.Gender);
                age = AgeEligibilityPolicy.Evaluate(demographics.DateOfBirth, DateOnly.FromDateTime(now.UtcDateTime));
                complete = true;
            }
            catch (EligibilityValidationException) { /* Invalid legacy data must fail closed. */ }
        }
        var policies = state.Policies.Any(x => x.Action == PolicyActions.TermsAgreed && x.Version == current.Terms.Version)
            && state.Policies.Any(x => x.Action == PolicyActions.PrivacyAcknowledged && x.Version == current.Privacy.Version);
        var canSelect = complete && policies && state.Membership is null;
        StarterModuleEligibility[] modules =
        [
            new(StarterModules.Learning, age.Learning, false, canSelect && age.Learning),
            new(StarterModules.Hobbies, age.Hobbies, false, canSelect && age.Hobbies),
            new(StarterModules.Social, age.Social, true, canSelect && age.Social)
        ];
        var membership = state.Membership is { } saved
            ? new StarterMembershipDto(saved.Module, saved.ActivatedAtUtc, saved.SocialOptInAtUtc is not null) : null;
        var next = !complete ? "complete_demographics" : !policies ? "review_policies"
            : membership is not null ? "starter_selected" : !age.Learning ? "age_ineligible" : "select_starter";
        return new(complete, policies, modules, membership, next);
    }

    private Guid RequireSubject()
    {
        if (!user.IsAuthenticated || user.UserId is not Guid id || id == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return id;
    }
    private async Task<Account> ResolveAccount(Guid subject, CancellationToken cancellationToken) =>
        await accounts.FindBySubjectAsync(subject, cancellationToken) ?? throw new StarterModuleAccountMissingException();
}
