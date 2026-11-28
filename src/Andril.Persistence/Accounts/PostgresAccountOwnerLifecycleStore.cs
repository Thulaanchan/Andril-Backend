using System.Data;
using System.Data.Common;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Andril.API")]

namespace Andril.Persistence.Accounts;

// A production source must participate in the same account revocation fence and supply
// current operation-specific security/eligibility. No default Satisfied provider exists.
internal sealed record AccountOwnerAuthority(AccountOwnerAuthorityOutcome Outcome, Guid ObservedVersion,
    Guid CurrentVersion, bool MayEstablishActive = false);
internal interface IAccountOwnerLifecycleAuthority
{
    Task<AccountOwnerAuthority> ReadAsync(Guid subject, Guid account, AccountOwnerOperation operation, CancellationToken cancellationToken);
}

internal sealed class PostgresAccountOwnerLifecycleStore(AccountsDbContext db, IOnboardingIdentity identity,
    IAccountOwnerLifecycleAuthority authority) : IAccountOwnerLifecycleStore
{
    public async Task<AccountOwnerLifecycleResult> ExecuteAsync(Guid subject, Guid requestKey, AccountOwnerOperation operation,
        Func<AccountOwnerLifecycleSnapshot, AccountOwnerLifecycleDecision> decide, CancellationToken cancellationToken)
    {
        RequireIdentity(subject);
        if (db.Database.CurrentTransaction is not null)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await BindAndLock(subject, cancellationToken);
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.SupabaseUserId == subject, cancellationToken)
                ?? throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account', {account.Id.ToString()}, true)", cancellationToken);
            var evidence = await ReadAuthority(subject, account.Id, operation, cancellationToken);
            RequireAuthority(evidence);
            var row = await db.Set<AccountOwnerStateRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, cancellationToken);
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
            if (row is null)
            {
                // No migration backfill or missing-source Active fallback. Existing
                // restrictions must be reconciled by the authoritative initializer.
                if (!evidence.MayEstablishActive)
                    throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.account_owner_states (account_id,state,revision,changed_at_utc) VALUES ({account.Id},'Active',1,{now})", cancellationToken);
                row = new() { AccountId = account.Id, State = "Active", Revision = 1, ChangedAtUtc = now };
            }
            var current = Result(row.State, row.Revision, row.ChangedAtUtc);
            var receipt = await db.Set<AccountOwnerReceiptRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.RequestKey == requestKey, cancellationToken);
            RequireIdentity(subject);
            var decision = decide(new(account, current, evidence.Outcome, evidence.ObservedVersion, evidence.CurrentVersion, now,
                receipt is null ? null : new(account.Id, requestKey, receipt.IntentHash, Result(receipt.State, receipt.Revision, receipt.ChangedAtUtc))));
            if (decision.Write is { } write)
            {
                var r = write.Receipt;
                var a = write.Audit;
                if (r.AccountId != account.Id || a.AccountId != account.Id || r.RequestKey != requestKey || a.RequestKey != requestKey
                    || a.Operation != operation || a.PreviousState != current.State || a.Result != decision.Result || r.Result != decision.Result
                    || decision.Result.Revision != current.Revision + 1 || decision.Result.ChangedAtUtc != now
                    || !AccountLifecyclePolicy.AllowsOwnerTransition(current.State, decision.Result.State))
                    throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);
                var state = decision.Result.State.ToString();
                var previous = current.State.ToString();
                var operationName = operation.ToString();
                var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_owner_states SET state={state},revision={decision.Result.Revision},changed_at_utc={now} WHERE account_id={account.Id} AND revision={current.Revision} AND state={previous}", cancellationToken);
                if (changed != 1) throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.account_owner_receipts (account_id,request_key,intent_hash,state,revision,changed_at_utc) VALUES ({account.Id},{requestKey},{r.IntentHash},{state},{decision.Result.Revision},{now})", cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.account_owner_audit (account_id,request_key,operation,previous_state,state,revision,changed_at_utc) VALUES ({account.Id},{requestKey},{operationName},{previous},{state},{decision.Result.Revision},{now})", cancellationToken);
            }
            else if (decision.Result != current || receipt is null)
                throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Conflict);
            var finalEvidence = await ReadAuthority(subject, account.Id, operation, cancellationToken);
            RequireAuthority(finalEvidence);
            if (finalEvidence.CurrentVersion != evidence.CurrentVersion)
                throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
            RequireIdentity(subject);
            cancellationToken.ThrowIfCancellationRequested();
            // No retry/reconciliation claiming success after a lost commit acknowledgement.
            await tx.CommitAsync(cancellationToken);
            return decision.Result;
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        }
    }

    internal async Task<AccountOwnerLifecycleResult> ReadAsync(Guid subject, CancellationToken cancellationToken)
    {
        RequireIdentity(subject);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await BindAndLock(subject, cancellationToken);
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.SupabaseUserId == subject, cancellationToken)
            ?? throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account', {account.Id.ToString()}, true)", cancellationToken);
        var row = await db.Set<AccountOwnerStateRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, cancellationToken)
            ?? throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        RequireIdentity(subject);
        await tx.CommitAsync(cancellationToken);
        return Result(row.State, row.Revision, row.ChangedAtUtc);
    }

    private Task BindAndLock(Guid subject, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({subject.ToString()},0)), set_config('andril.subject',{subject.ToString()},true)", ct);

    private async Task<AccountOwnerAuthority> ReadAuthority(Guid subject, Guid account, AccountOwnerOperation operation, CancellationToken ct)
    {
        try
        {
            return await authority.ReadAsync(subject, account, operation, ct)
                ?? throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AccountOwnerLifecycleException) { throw; }
        catch { throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable); }
    }

    private void RequireIdentity(Guid subject)
    {
        if (subject == Guid.Empty || !identity.IsConfirmedFor(subject))
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.AuthenticationRequired);
    }

    private static void RequireAuthority(AccountOwnerAuthority value)
    {
        if (value.Outcome == AccountOwnerAuthorityOutcome.Unavailable || value.CurrentVersion == Guid.Empty)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        if (value.Outcome != AccountOwnerAuthorityOutcome.Satisfied || value.ObservedVersion != value.CurrentVersion)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
    }

    private static AccountOwnerLifecycleResult Result(string state, long revision, DateTimeOffset changed)
    {
        if (!Enum.TryParse<AccountLifecycleState>(state, out var parsed) || !Enum.IsDefined(parsed) || revision <= 0)
            throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Unavailable);
        return new(parsed, revision, changed);
    }
}
