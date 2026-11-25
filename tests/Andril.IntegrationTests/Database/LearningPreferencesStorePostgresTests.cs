using System.Data;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Real PostgreSQL data-component tests. Synthetic authority is TEST ONLY; no
/// production coordination, ownership FK/RLS, durable revision contract or DI is supplied.</summary>
public sealed class LearningPreferencesStorePostgresTests
{
    private sealed class Authority : ICurrentUser, ILearningSetupAuthorityReader
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        internal readonly Guid Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Version = Guid.NewGuid();
        internal bool Revoked, Unavailable;
        internal LearningSetupScope Scope => new(Account, Profile);
        internal LearningSetupAccessBoundary Boundary => new(this, this);
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningSetupAccessEvidence?>(Unavailable ? null : new(subject, Account, Profile, Account,
                "learning", operation, true, true, true, true, true, Revoked, false, Version, Version));
        }
    }

    private static LearningDbContext Open(LocalAccountDatabase fixture)
    {
        using var accounts = fixture.Open();
        return new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(accounts.Database.GetConnectionString()).Options);
    }

    private static async Task CreateModel(LocalAccountDatabase fixture)
    {
        await using var db = Open(fixture);
        await db.Database.MigrateAsync();
    }

    private static async Task Sql(LearningDbContext db, string sql)
    {
        // Role names below are generated exclusively from a fixed prefix and server GUIDs.
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LearningSetupReadResult> Read(LocalAccountDatabase fixture, LearningSetupScope scope)
    {
        await using var db = Open(fixture);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var result = await new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(scope);
        await tx.CommitAsync(); return result;
    }

    private static LearningSkillCatalogue Catalogue(params Guid[] skills) => new(skills.Select(s => new LearningSkillCatalogue.Skill(s, true, LearningSkillCatalogueStatus.Active)));

    private static async Task<LearningSetupSnapshot> Seed(LocalAccountDatabase fixture, LearningSetupScope scope, Guid skill)
    {
        await LearningPostgresFixture.Profile(fixture, scope);
        var p = new LearningPreferences(scope.AccountId, scope.LearningProfileId); p.UpdateSelections([], [skill], []);
        var goal = p.CreateGoal("synthetic-private-goal");
        await using var db = Open(fixture);
        db.SkillSelections.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Teaching));
        db.PersonalGoals.Add(LearningPersonalGoal.CreateFromPreferences(p, goal.Id)); await db.SaveChangesAsync();
        return (await Read(fixture, scope)).Snapshot!;
    }

    private static LearningSetupSnapshot Replace(LearningSetupSnapshot before, params Guid[] learning)
    {
        var p = new LearningPreferences(before.Scope.AccountId, before.Scope.LearningProfileId);
        p.UpdateSelections([], before.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId), learning);
        var rows = before.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching)
            .Select(s => LearningSkillSelection.CreateFromPreferences(s.Id, p, s.SkillId, s.Kind))
            .Concat(learning.Select(s => LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, s, LearningSkillSelectionKind.Learning))).ToArray();
        var method = typeof(LearningSetupSnapshot).GetMethod("WithSelections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (LearningSetupSnapshot)method.Invoke(before, [rows])!;
    }

    [LocalPostgresFact]
    public async Task DataComponentRequiresExplicitExternalTransactionAndHasNoProductionRepositoryContract()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        await using var db = Open(fixture); var a = new Authority();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(a.Scope));
        Assert.False(typeof(ILearningSetupRepository).IsAssignableFrom(typeof(IsolatedLearningPreferencesStore)));
        Assert.False(typeof(IsolatedLearningPreferencesStore).IsVisible);
        var classify = typeof(IsolatedLearningPreferencesStore).GetMethod("IsConflict",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Exception Wrapped(string state) => new InvalidOperationException("synthetic wrapper",
            new DbUpdateException("synthetic update", new PostgresException("synthetic", "ERROR", "ERROR", state)));
        Assert.True((bool)classify.Invoke(null, [Wrapped(PostgresErrorCodes.SerializationFailure)])!);
        Assert.False((bool)classify.Invoke(null, [Wrapped(PostgresErrorCodes.InsufficientPrivilege)])!);
        Assert.False((bool)classify.Invoke(null, [Wrapped(PostgresErrorCodes.UndefinedTable)])!);
        Assert.False((bool)classify.Invoke(null, [new InvalidOperationException("synthetic unrelated failure")])!);
        Assert.False((bool)classify.Invoke(null, [new OperationCanceledException()])!);
    }

    [LocalPostgresFact]
    public async Task ScopedReadAndReplacementPreserveForeignProfileGoalsAndExistingSelectionIdentities()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var b = new Authority(); var skill = Guid.NewGuid();
        await Seed(fixture, a.Scope, skill); var foreign = await Seed(fixture, b.Scope, skill);
        var before = await Read(fixture, a.Scope); var replacement = Replace(before.Snapshot!, skill);
        await using (var db = Open(fixture))
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Prepared,
                await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(replacement, before.StorageRevision!, Catalogue(skill)));
            await tx.CommitAsync();
        }
        var after = (await Read(fixture, a.Scope)).Snapshot!;
        Assert.Equal(2, after.Selections.Count); Assert.Equal(before.Snapshot!.Selections[0].Id, after.Selections.Single(s => s.Kind == LearningSkillSelectionKind.Teaching).Id);
        Assert.Equal(before.Snapshot.Goals.Select(g => (g.Id, g.Text)), after.Goals.Select(g => (g.Id, g.Text)));
        var untouched = (await Read(fixture, b.Scope)).Snapshot!;
        Assert.Equal(foreign.Selections.Select(s => (s.Id, s.SkillId)), untouched.Selections.Select(s => (s.Id, s.SkillId)));
        Assert.Equal(foreign.Goals.Select(g => (g.Id, g.Text)), untouched.Goals.Select(g => (g.Id, g.Text)));
    }

    [LocalPostgresFact]
    public async Task PreparedReplacementAndCancellationRollbackActualDatabaseChanges()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        var before = await Read(fixture, a.Scope);
        await using (var db = Open(fixture))
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var store = new IsolatedLearningPreferencesStore(db);
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Prepared,
                await store.PrepareReplacementAsync(Replace(before.Snapshot!, skill), before.StorageRevision!, Catalogue(skill)));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadUnderTransactionAsync(a.Scope, cts.Token));
            await tx.RollbackAsync();
        }
        var after = (await Read(fixture, a.Scope)).Snapshot!;
        Assert.Equal(before.Snapshot!.Selections.Select(s => s.Id), after.Selections.Select(s => s.Id));
        Assert.Equal(before.Snapshot.Goals.Select(g => (g.Id, g.Text)), after.Goals.Select(g => (g.Id, g.Text)));
    }

    [LocalPostgresFact]
    public async Task SeparateConnectionsWithSameRevisionProduceOneCommitAndOneConflict()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        var before = await Read(fixture, a.Scope); var first = Replace(before.Snapshot!, skill); var second = Replace(before.Snapshot!, skill);
        await using var left = Open(fixture); await using var right = Open(fixture);
        await left.Database.OpenConnectionAsync(); await right.Database.OpenConnectionAsync();
        Assert.NotEqual(((NpgsqlConnection)left.Database.GetDbConnection()).ProcessID, ((NpgsqlConnection)right.Database.GetDbConnection()).ProcessID);
        async Task<IsolatedLearningPreferencesStore.ReplacementState> Update(LearningDbContext db, LearningSetupSnapshot snapshot)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var result = await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(snapshot, before.StorageRevision!, Catalogue(skill));
            if (result == IsolatedLearningPreferencesStore.ReplacementState.Prepared)
            {
                var committed = await new IsolatedLearningPreferencesStore(db).CommitPreparedAsync();
                if (committed != LearningSetupWriteOutcome.Committed)
                {
                    Assert.Equal(LearningSetupWriteOutcome.Conflict, committed);
                    return IsolatedLearningPreferencesStore.ReplacementState.Conflict;
                }
            }
            else await tx.RollbackAsync();
            return result;
        }
        var results = await Task.WhenAll(Update(left, first), Update(right, second));
        Assert.Single(results, r => r == IsolatedLearningPreferencesStore.ReplacementState.Prepared);
        Assert.Single(results, r => r == IsolatedLearningPreferencesStore.ReplacementState.Conflict);
        Assert.Equal(2, (await Read(fixture, a.Scope)).Snapshot!.Selections.Count);
    }

    [LocalPostgresFact]
    public async Task StaleRevisionAndChangedExistingIdentityRejectWithoutMutation()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        var before = await Read(fixture, a.Scope);
        await using (var db = Open(fixture))
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Conflict,
                await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(Replace(before.Snapshot!, skill), "stale", Catalogue(skill)));
            await tx.RollbackAsync();
        }
        var preferences = new LearningPreferences(a.Account, a.Profile); preferences.UpdateSelections([], [skill], []);
        var altered = new LearningSetupSnapshot(a.Scope,
            [LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, skill, LearningSkillSelectionKind.Teaching)], []);
        await using (var db = Open(fixture))
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Conflict,
                await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(altered, before.StorageRevision!, Catalogue(skill)));
            await tx.RollbackAsync();
        }
        Assert.Single((await Read(fixture, a.Scope)).Snapshot!.Selections);
    }

    [LocalPostgresFact]
    public async Task SyntheticOwnerDenialUnavailableAndRevocationCannotCommitPreparedChanges()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        a.Revoked = true;
        Assert.Equal(LearningSetupAccessState.Denied, (await a.Boundary.CheckAsync(LearningSetupOperation.UpdateSetup)).State);
        a.Unavailable = true;
        Assert.Equal(LearningSetupAccessState.Unavailable, (await a.Boundary.CheckAsync(LearningSetupOperation.UpdateSetup)).State);
        a.Revoked = false; a.Unavailable = false;
        var grant = await a.Boundary.CheckAsync(LearningSetupOperation.UpdateSetup);
        Assert.Equal(a.Profile, grant.Scope!.LearningProfileId); Assert.Equal(a.Account, grant.Scope.AccountId);
        var before = await Read(fixture, grant.Scope);
        await using (var db = Open(fixture))
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Prepared,
                await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(Replace(before.Snapshot!, skill), before.StorageRevision!, Catalogue(skill)));
            a.Revoked = true;
            Assert.Equal(LearningSetupAccessState.Denied, (await a.Boundary.CheckAsync(LearningSetupOperation.UpdateSetup)).State);
            await tx.RollbackAsync();
        }
        Assert.Single((await Read(fixture, a.Scope)).Snapshot!.Selections);
        // Caller-owned synthetic checks/rollback only, not proof of production revocation fencing.
    }

    [LocalPostgresFact]
    public async Task DatabaseRejectsDuplicatesAndRestrictedRuntimeCannotWriteGoalsOrReadAccounts()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        await using var db = Open(fixture); await db.Database.OpenConnectionAsync();
        var role = "andril_learning_store_" + Guid.NewGuid().ToString("N");
        await Sql(db, $"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS");
        try
        {
            await Sql(db, $"GRANT {role} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE; GRANT USAGE ON SCHEMA andril_learning TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA andril_learning TO {role}; GRANT INSERT,DELETE ON andril_learning.learning_skill_selections TO {role}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_profile_revision(uuid,uuid) TO {role}; SELECT set_config('andril.subject','{a.Account}',false),set_config('andril.account','{a.Account}',false),set_config('andril.module','learning',false); SET ROLE {role}");
            var before = await Read(fixture, a.Scope);
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Prepared,
                    await new IsolatedLearningPreferencesStore(db).PrepareReplacementAsync(Replace(before.Snapshot!, skill), before.StorageRevision!, Catalogue(skill)));
                await tx.CommitAsync();
            }
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_skill_selections(id,learning_profile_id,module,skill_id,kind) VALUES ({Guid.NewGuid()},{a.Profile},'learning',{skill},1)"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            foreach (var sql in new[] { "UPDATE andril_learning.learning_personal_goals SET text='forbidden'", "SELECT * FROM andril_identity.accounts" })
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        }
        finally
        {
            await Sql(db, $"RESET ROLE; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}");
        }
    }

    [LocalPostgresFact]
    public async Task SerializableGoalSnapshotRemainsConsistentAcrossIndependentGoalCommit()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        await using var reader = Open(fixture); await using var writer = Open(fixture);
        await using var readTx = await reader.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var store = new IsolatedLearningPreferencesStore(reader);
        var before = await store.ReadUnderTransactionAsync(a.Scope);
        await using (var writeTx = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.NotEqual(((NpgsqlConnection)reader.Database.GetDbConnection()).ProcessID,
                ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID);
            // Deterministic ordering: reader snapshot exists, then independent goal writer
            // commits, then reader repeats both queries. No timing sleeps or mock scheduling.
            await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_personal_goals SET text='concurrently changed' WHERE learning_profile_id={a.Profile} AND module='learning'");
            await writeTx.CommitAsync();
        }
        var repeated = await store.ReadUnderTransactionAsync(a.Scope);
        Assert.Equal(before.StorageRevision, repeated.StorageRevision);
        Assert.Equal(before.Snapshot!.Goals.Select(g => (g.Id, g.Text)), repeated.Snapshot!.Goals.Select(g => (g.Id, g.Text)));
        Assert.Equal(before.Snapshot.Selections.Select(s => s.Id), repeated.Snapshot.Selections.Select(s => s.Id));
        await readTx.CommitAsync();
        var fresh = await Read(fixture, a.Scope);
        Assert.Equal("concurrently changed", Assert.Single(fresh.Snapshot!.Goals).Text);
        Assert.NotEqual(before.StorageRevision, fresh.StorageRevision);
        await using var stale = Open(fixture);
        await using var staleTx = await stale.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Conflict,
            await new IsolatedLearningPreferencesStore(stale).PrepareReplacementAsync(Replace(before.Snapshot, skill), before.StorageRevision!, Catalogue(skill)));
        await staleTx.RollbackAsync();
    }

    [LocalPostgresFact]
    public async Task ConcurrentGoalSelectionWriteSkewReturnsConflictWithoutRetryOrPartialSelectionCommit()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await CreateModel(fixture);
        var a = new Authority(); var skill = Guid.NewGuid(); await Seed(fixture, a.Scope, skill);
        await using var reader = Open(fixture); await using var writer = Open(fixture);
        await using var readTx = await reader.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var store = new IsolatedLearningPreferencesStore(reader);
        var before = await store.ReadUnderTransactionAsync(a.Scope);
        await using (var writeTx = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.NotEqual(((NpgsqlConnection)reader.Database.GetDbConnection()).ProcessID,
                ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID);
            // Opposing read/write dependencies: reader observes goal; writer observes
            // selections then commits goal edit; reader attempts selection insertion.
            Assert.Single(await writer.SkillSelections.AsNoTracking().Where(s => s.LearningProfileId == a.Profile).ToArrayAsync());
            await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_personal_goals SET text='goal writer committed' WHERE learning_profile_id={a.Profile} AND module='learning'");
            await writeTx.CommitAsync();
        }
        var prepared = await store.PrepareReplacementAsync(Replace(before.Snapshot!, skill), before.StorageRevision!, Catalogue(skill));
        if (prepared == IsolatedLearningPreferencesStore.ReplacementState.Prepared)
            Assert.Equal(LearningSetupWriteOutcome.Conflict, await store.CommitPreparedAsync());
        else
        {
            Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Conflict, prepared);
            await readTx.RollbackAsync();
        }
        var fresh = await Read(fixture, a.Scope);
        Assert.Single(fresh.Snapshot!.Selections);
        Assert.Equal(before.Snapshot!.Selections[0].Id, fresh.Snapshot.Selections[0].Id);
        Assert.Equal("goal writer committed", Assert.Single(fresh.Snapshot.Goals).Text);
    }
}
