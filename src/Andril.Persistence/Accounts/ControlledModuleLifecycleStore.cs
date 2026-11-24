using System.Data;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Accounts;

// No implementation in the application asserts that missing standing, withdrawal or
// revocation sources are satisfied. Only the isolated test host supplies synthetic evidence.
internal sealed record LifecycleAuthorityState(ModuleAuthorizationOutcome Outcome, bool? RestrictionsAllow,
    bool? SocialOptIn, Guid ObservedVersion, Guid CurrentVersion);
internal interface IControlledLifecycleAuthority
{
    Task<LifecycleAuthorityState> ReadAsync(Guid subject, Guid account, string module, CancellationToken cancellationToken);
}

internal sealed class UnavailableLifecycleAuthority : IControlledLifecycleAuthority
{
    public Task<LifecycleAuthorityState> ReadAsync(Guid subject, Guid account, string module, CancellationToken cancellationToken) =>
        Task.FromResult(new LifecycleAuthorityState(ModuleAuthorizationOutcome.Unavailable, null, null, Guid.Empty, Guid.Empty));
}

// Deliberately internal and never registered by production startup. The only executable
// composition lives in the integration test assembly, with a random disposable database.
internal sealed class ControlledModuleLifecycleStore : IModuleLifecycleStore
{
    private readonly string connection;
    private readonly IOnboardingIdentity identity;
    private readonly IPolicyCatalogProvider policies;
    private readonly IControlledLifecycleAuthority authority;

    internal ControlledModuleLifecycleStore(string connection, IOnboardingIdentity identity,
        IPolicyCatalogProvider policies, IControlledLifecycleAuthority? authority, bool explicitlyOptedIn)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (!explicitlyOptedIn || settings.Host is not ("localhost" or "127.0.0.1" or "::1")
            || settings.Database is not { } name || !name.StartsWith("andril_m2_test_", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["andril_m2_test_".Length..], "N", out _))
            throw new InvalidOperationException("Lifecycle integration requires an opted-in disposable local test database.");
        settings.IncludeErrorDetail = false;
        settings.NoResetOnClose = false;
        this.connection = settings.ConnectionString;
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.authority = authority ?? new UnavailableLifecycleAuthority();
    }

    public Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid requestKey,
        Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken cancellationToken) =>
        InTransaction(subject, module, requestKey, decide, cancellationToken);

    public async Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid requestKey, CancellationToken cancellationToken)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await SetSubject(db, subject, module, cancellationToken);
        var snapshot = await Snapshot(db, subject, module, requestKey, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return snapshot;
    }

    private async Task<ModuleLifecycleResult> InTransaction(Guid subject, string module, Guid requestKey,
        Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken ct)
    {
        await using var db = Open();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SetSubject(db, subject, module, ct);
        // One transaction-scoped account lock precedes every module/receipt access. It also
        // covers an absent initial row; hash collisions only serialize unrelated requests.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({subject.ToString()}, 0))", ct);
        var snapshot = await Snapshot(db, subject, module, requestKey, ct);
        var decision = decide(snapshot);
        if (decision.Write is { } write)
        {
            var scope = snapshot.Evidence.Scope;
            if (write.Receipt.Scope != scope || write.Receipt.RequestKey != requestKey
                || write.Audit is { } a && a.Scope != scope
                || write.ChangedMembership is { } m && m.Membership.Scope != scope)
                throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidScope);
            var account = scope.AccountId;
            if (write.ChangedMembership is { } changed)
            {
                var revision = changed.Membership.Revision;
                var now = changed.StateChangedAtUtc;
                int rows;
                if (snapshot.Current is null)
                    rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO andril_identity.account_module_lifecycles
                        (account_id,module,state,revision,first_activated_at_utc,state_changed_at_utc)
                        VALUES ({account},{module},'active',{revision},{now},{now})
                        ON CONFLICT (account_id,module) DO NOTHING
                        """, ct);
                else
                {
                    var oldRevision = snapshot.Current.Membership.Revision;
                    rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE andril_identity.account_module_lifecycles SET state='active',revision={revision},state_changed_at_utc={now}
                        WHERE account_id={account} AND module={module} AND state='inactive' AND revision={oldRevision}
                        """, ct);
                }
                if (rows != 1) throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);
            }
            var r = write.Receipt;
            var operation = r.Operation == ModuleLifecycleOperation.Activate ? "activate" : "reactivate";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_module_lifecycle_receipts
                (account_id,module,request_key,operation,expected_revision,intent_hash,revision,state_changed_at_utc,recorded_at_utc)
                VALUES ({account},{module},{requestKey},{operation},{r.ExpectedRevision},{r.IntentHash},{r.Result.Revision},{r.Result.StateChangedAtUtc},{r.RecordedAtUtc})
                """, ct);
            if (write.Audit is { } audit)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.account_module_lifecycle_audit
                    (account_id,module,request_key,revision,operation,previous_state,correlation_id,recorded_at_utc)
                    VALUES ({account},{module},{requestKey},{audit.Result.Revision},{operation},{audit.PreviousState},{audit.CorrelationId},{r.RecordedAtUtc})
                    """, ct);
        }
        // Exceptions before this point roll back all three writes. A lost commit reply is
        // never treated as a confirmed failure or success; the coordinator reads a receipt.
        try { await tx.CommitAsync(ct); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or OperationCanceledException)
        { throw new ModuleLifecycleCommitUnknownException(); }
        return decision.Result;
    }

    private AccountsDbContext Open() => new(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(connection).Options);

    private static Task SetSubject(AccountsDbContext db, Guid subject, string module, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject', {subject.ToString()}, true), set_config('andril.module', {module}, true)", ct);

    private async Task<ModuleLifecycleSnapshot> Snapshot(AccountsDbContext db, Guid subject, string module, Guid key, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.SupabaseUserId == subject, ct)
            ?? throw new ModuleLifecycleException(ModuleLifecycleDenial.UnconfirmedAccount);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account', {account.Id.ToString()}, true)", ct);
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var state = await authority.ReadAsync(subject, account.Id, module, ct);
        var demographics = await db.Demographics.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
        var receipts = await db.PolicyRecords.AsNoTracking().Where(x => x.AccountId == account.Id).ToListAsync(ct);
        PolicyCatalog? catalog;
        try { catalog = policies.GetCurrent(); }
        catch (PolicyConfigurationException) { catalog = null; }
        var scope = new ModuleMembershipScope(account.Id, module);
        var evidence = new ModuleActivationEvidence(scope, state.Outcome, account,
            identity.IsConfirmedFor(subject) ? subject : null, demographics, catalog, receipts,
            state.RestrictionsAllow, state.SocialOptIn, state.ObservedVersion, state.CurrentVersion, now);
        var row = await db.Set<ModuleLifecycleRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.Module == module, ct);
        StoredModuleMembership? current = row is null ? null : new(row.State == "active"
            ? ActiveModuleMembership.Restore(scope, row.Revision) : new InactiveModuleMembership(scope, row.Revision), row.StateChangedAtUtc);
        var stored = await db.Set<ModuleLifecycleReceiptRow>().AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.Module == module && x.RequestKey == key, ct);
        var receipt = stored is null ? null : new ModuleLifecycleReceipt(scope, key,
            stored.Operation == "activate" ? ModuleLifecycleOperation.Activate : ModuleLifecycleOperation.Reactivate,
            stored.ExpectedRevision, stored.IntentHash, new(module, stored.Revision, stored.StateChangedAtUtc), stored.RecordedAtUtc);
        return new(evidence, current, receipt);
    }
}
