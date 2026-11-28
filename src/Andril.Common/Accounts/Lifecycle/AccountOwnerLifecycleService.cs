using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Andril.Common.Accounts.Onboarding;
using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Lifecycle;

internal enum AccountOwnerOperation { Deactivate, Reactivate, Delete }
internal enum AccountOwnerAuthorityOutcome { Unavailable, Denied, Satisfied }
internal enum AccountOwnerLifecycleDenial { AuthenticationRequired, Unavailable, Denied, Conflict, ConfirmationRequired }
internal sealed class AccountOwnerLifecycleException(AccountOwnerLifecycleDenial denial) : Exception
{
    internal AccountOwnerLifecycleDenial Denial { get; } = denial;
}

internal sealed record AccountOwnerLifecycleCommand(AccountOwnerOperation Operation, Guid RequestKey,
    long ExpectedRevision, bool ConfirmDeletion = false);
internal sealed record AccountOwnerLifecycleResult(AccountLifecycleState State, long Revision, DateTimeOffset ChangedAtUtc);
internal sealed record AccountOwnerLifecycleReceipt(Guid AccountId, Guid RequestKey, string IntentHash,
    AccountOwnerLifecycleResult Result);
internal sealed record AccountOwnerLifecycleAudit(Guid AccountId, Guid RequestKey, AccountOwnerOperation Operation,
    AccountLifecycleState PreviousState, AccountOwnerLifecycleResult Result);
internal sealed record AccountOwnerLifecycleSnapshot(Account Account, AccountOwnerLifecycleResult Current,
    AccountOwnerAuthorityOutcome Authority, Guid ObservedAuthorityVersion, Guid CurrentAuthorityVersion,
    DateTimeOffset DatabaseTime, AccountOwnerLifecycleReceipt? Receipt = null);
internal sealed record AccountOwnerLifecycleWrite(AccountOwnerLifecycleReceipt Receipt, AccountOwnerLifecycleAudit Audit);
internal sealed record AccountOwnerLifecycleDecision(AccountOwnerLifecycleResult Result, AccountOwnerLifecycleWrite? Write = null);

// New technical boundary, not an existing production authority. An adapter must resolve
// the account from the verified subject, serialize every account/admin lifecycle writer,
// read current operation-specific security/eligibility and revision inside that fence,
// and commit state, receipt and audit atomically. Satisfied is never inferred from a
// missing source, an old JWT or a module membership. Unknown commits must propagate;
// callers must not automatically retry. No production adapter or DI binding exists yet.
internal interface IAccountOwnerLifecycleStore
{
    Task<AccountOwnerLifecycleResult> ExecuteAsync(Guid subject, Guid requestKey, AccountOwnerOperation operation,
        Func<AccountOwnerLifecycleSnapshot, AccountOwnerLifecycleDecision> decide, CancellationToken cancellationToken);
}

internal sealed class AccountOwnerLifecycleService(ICurrentUser caller, IOnboardingIdentity identity,
    IAccountOwnerLifecycleStore store)
{
    internal async Task<AccountOwnerLifecycleResult> ExecuteAsync(AccountOwnerLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        var subject = RequireIdentity(cancellationToken);
        if (!Enum.IsDefined(command.Operation) || command.RequestKey == Guid.Empty
            || command.ExpectedRevision is <= 0 or long.MaxValue)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);
        if (command.Operation == AccountOwnerOperation.Delete && !command.ConfirmDeletion)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.ConfirmationRequired);
        if (command.Operation != AccountOwnerOperation.Delete && command.ConfirmDeletion)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);

        var intent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"v1\n{command.Operation}\n{command.ExpectedRevision.ToString(CultureInfo.InvariantCulture)}\n{command.ConfirmDeletion}")));
        var result = await store.ExecuteAsync(subject, command.RequestKey, command.Operation, snapshot =>
        {
            if (RequireIdentity(cancellationToken) != subject)
                throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.AuthenticationRequired);
            return Decide(snapshot, subject, command, intent);
        }, cancellationToken);
        if (RequireIdentity(cancellationToken) != subject)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.AuthenticationRequired);
        return result;
    }

    private Guid RequireIdentity(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!caller.IsAuthenticated || caller.UserId is not Guid subject || subject == Guid.Empty
            || !identity.IsConfirmedFor(subject))
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.AuthenticationRequired);
        return subject;
    }

    private static AccountOwnerLifecycleDecision Decide(AccountOwnerLifecycleSnapshot snapshot, Guid subject,
        AccountOwnerLifecycleCommand command, string intent)
    {
        if (snapshot.Account.Id == Guid.Empty || snapshot.Account.SupabaseUserId != subject)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
        if (snapshot.Authority == AccountOwnerAuthorityOutcome.Unavailable
            || snapshot.CurrentAuthorityVersion == Guid.Empty)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        if (snapshot.Authority != AccountOwnerAuthorityOutcome.Satisfied
            || snapshot.ObservedAuthorityVersion != snapshot.CurrentAuthorityVersion)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
        if (!Enum.IsDefined(snapshot.Current.State) || snapshot.Current.Revision <= 0)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);

        var target = command.Operation switch
        {
            AccountOwnerOperation.Deactivate => AccountLifecycleState.Deactivated,
            AccountOwnerOperation.Reactivate => AccountLifecycleState.Active,
            AccountOwnerOperation.Delete => AccountLifecycleState.Deleted,
            _ => throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict)
        };

        // Replays require fresh authority and the exact still-current result. An old
        // receipt can never restore access or report a superseded Active state.
        if (snapshot.Receipt is { } receipt)
        {
            if (receipt.AccountId != snapshot.Account.Id || receipt.RequestKey != command.RequestKey
                || receipt.IntentHash != intent || receipt.Result != snapshot.Current
                || receipt.Result.State != target || receipt.Result.Revision != command.ExpectedRevision + 1)
                throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);
            return new(receipt.Result);
        }

        if (!AccountLifecyclePolicy.AllowsOwnerTransition(snapshot.Current.State, target))
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
        if (snapshot.Current.Revision != command.ExpectedRevision || snapshot.Current.Revision == long.MaxValue
            || snapshot.DatabaseTime < snapshot.Current.ChangedAtUtc)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);

        var result = new AccountOwnerLifecycleResult(target, checked(snapshot.Current.Revision + 1),
            snapshot.DatabaseTime.ToUniversalTime());
        return new(result, new(new(snapshot.Account.Id, command.RequestKey, intent, result),
            new(snapshot.Account.Id, command.RequestKey, command.Operation, snapshot.Current.State, result)));
    }
}
