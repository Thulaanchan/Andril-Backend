using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Policies;

public sealed class AccountPolicyService(ICurrentUser user, IAccountRepository accounts,
    IAccountPolicyRepository records, IPolicyCatalogProvider catalog, TimeProvider clock) : IAccountPolicyService
{
    public async Task<PolicyStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        RequireSubject();
        var current = catalog.GetCurrent();
        var account = await ResolveAccount(cancellationToken);
        return Status(current, await records.ReadAsync(account.Id, cancellationToken));
    }

    public async Task<PolicyStatus> AcknowledgeAsync(IReadOnlyList<PolicyActionSubmission>? actions,
        CancellationToken cancellationToken = default)
    {
        RequireSubject();
        var current = catalog.GetCurrent();
        if (actions is null || actions.Count != 2 || actions.Any(x => x is null || !AccountPolicyRecord.ValidVersion(x.Version))
            || actions.Count(x => x.Action == PolicyActions.TermsAgreed) != 1
            || actions.Count(x => x.Action == PolicyActions.PrivacyAcknowledged) != 1)
            throw new PolicyValidationException();
        foreach (var action in actions)
            if (action.Version != (action.Action == PolicyActions.TermsAgreed ? current.Terms.Version : current.Privacy.Version))
                throw new PolicyVersionConflictException();
        var account = await ResolveAccount(cancellationToken);
        await records.AppendAsync(account.Id, actions, clock.GetUtcNow(), cancellationToken);
        return Status(current, await records.ReadAsync(account.Id, cancellationToken));
    }

    private static PolicyStatus Status(PolicyCatalog current, IReadOnlyList<AccountPolicyRecord> records)
    {
        var terms = records.Any(x => x.Action == PolicyActions.TermsAgreed && x.Version == current.Terms.Version);
        var privacy = records.Any(x => x.Action == PolicyActions.PrivacyAcknowledged && x.Version == current.Privacy.Version);
        return new(current, terms, privacy, terms && privacy,
            records.Select(x => new PolicyReceipt(x.Action, x.Version, x.RecordedAtUtc)).ToArray());
    }
    private Guid RequireSubject()
    {
        if (!user.IsAuthenticated || user.UserId is not Guid id || id == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return id;
    }
    private async Task<Account> ResolveAccount(CancellationToken cancellationToken) =>
        await accounts.FindBySubjectAsync(RequireSubject(), cancellationToken) ?? throw new PolicyAccountMissingException();
}
