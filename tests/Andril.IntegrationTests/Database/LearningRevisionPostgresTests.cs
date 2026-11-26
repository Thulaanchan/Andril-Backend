using System.Data;
using System.Data.Common;
using System.Reflection;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>REV-01..28 and FD-03 against PostgreSQL, including real independent transactions.
/// Only session/standing/catalogue authorities are synthetic. No hosted database or runtime DI.
/// Each case owns a disposable database through the unchanged M2 fixture.</summary>
public sealed class LearningRevisionPostgresTests
{
    private sealed class Authority : ICurrentUser, ILearningSetupAuthorityReader, ILearningSkillCatalogueReader
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        internal readonly LearningSetupScope Scope = new(Guid.NewGuid(), Guid.NewGuid());
        internal readonly Guid Skill = Guid.NewGuid(), SecondSkill = Guid.NewGuid(), Version = Guid.NewGuid();
        internal bool Revoked, Unavailable, Standing = true;
        internal string Module = "learning", CatalogueVersion = "catalogue-v1";
        internal LearningSetupAccessBoundary Boundary => new(this, this);
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningSetupAccessEvidence?>(Unavailable ? null : new(subject, Scope.AccountId,
                Scope.LearningProfileId, Scope.AccountId, Module, operation, true, true, Standing, true, true,
                Revoked, false, Version, Version));
        }
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Unavailable ? LearningSkillCatalogueReadResult.Unavailable() : LearningSkillCatalogueReadResult.Available(
                new([new(Skill, true, LearningSkillCatalogueStatus.Active), new(SecondSkill, true, LearningSkillCatalogueStatus.Active)]), CatalogueVersion));
        }
    }

    private sealed class World(LocalAccountDatabase fixture, Authority authority) : IAsyncDisposable
    {
        internal LocalAccountDatabase Fixture => fixture;
        internal Authority Authority => authority;
        internal LearningSetupScope Scope => authority.Scope;
        internal LearningDbContext Open() => LearningPostgresFixture.Open(fixture);
        internal DbContextOptions<LearningDbContext> Options(params IInterceptor[] interceptors)
        {
            using var db = Open();
            return new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptors).Options;
        }
        internal IsolatedLearningSetupRepository Repository(Func<string, CancellationToken, Task>? hook = null, params IInterceptor[] interceptors) =>
            new(Options(interceptors), authority, authority.Boundary, authority) { TestCheckpoint = hook };
        internal async Task<LearningSetupReadResult> Read()
        {
            var read = await Repository().ReadAsync(Scope);
            Assert.Equal(LearningSetupReadState.Available, read.State);
            Assert.NotNull(read.Snapshot); Assert.True(LearningSetupRevision.TryParse(read.StorageRevision, out _));
            return read;
        }
        internal async Task Sql(FormattableString sql)
        {
            await using var db = Open();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            // Fixture-owner lifecycle operations obey the existing FORCE RLS scope. This is
            // private setup, not a fabricated authorization source for restricted runtime tests.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{authority.UserId!.Value.ToString()},true),set_config('andril.account',{Scope.AccountId.ToString()},true),set_config('andril.module','learning',true)");
            await db.Database.ExecuteSqlInterpolatedAsync(sql); await tx.CommitAsync();
        }
        internal Task Goal(string text = "SYNTHETIC-PRIVATE-GOAL") => Sql($"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES({Guid.NewGuid()},{Scope.LearningProfileId},'learning',{text})");
        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    private static async Task<World> Create()
    {
        var fixture = await LocalAccountDatabase.CreateAsync();
        try
        {
            await LearningPostgresFixture.Migrate(fixture);
            var authority = new Authority();
            await LearningPostgresFixture.Profile(fixture, authority.Scope, authority.UserId);
            return new(fixture, authority);
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static LearningSetupRevision Revision(LearningSetupReadResult read)
    { Assert.True(LearningSetupRevision.TryParse(read.StorageRevision, out var value)); return value!; }

    private static LearningSetupSnapshot Select(LearningSetupSnapshot before, params Guid[] skills)
    {
        var p = new LearningPreferences(before.Scope.AccountId, before.Scope.LearningProfileId);
        p.UpdateSelections([], before.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId), skills);
        var rows = before.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching)
            .Select(s => LearningSkillSelection.CreateFromPreferences(s.Id, p, s.SkillId, s.Kind))
            .Concat(skills.Select(skill => LearningSkillSelection.CreateFromPreferences(
                before.Selections.SingleOrDefault(s => s.Kind == LearningSkillSelectionKind.Learning && s.SkillId == skill)?.Id ?? Guid.NewGuid(),
                p, skill, LearningSkillSelectionKind.Learning))).ToArray();
        // Existing private immutable-copy convention; do not reconstruct/change historical goals.
        return (LearningSetupSnapshot)typeof(LearningSetupSnapshot).GetMethod("WithSelections", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(before, [rows])!;
    }

    private static Task<LearningSetupWriteOutcome> Change(World w, LearningSetupReadResult before, params Guid[] skills) =>
        w.Repository().ReplaceAsync(Select(before.Snapshot!, skills), before.StorageRevision!, w.Authority.CatalogueVersion);

    private static async Task FixtureDdl(LearningDbContext db, string sql)
    {
        // Identifiers and passwords are exclusively fixed prefixes/server GUIDs, never client data.
        // PostgreSQL role DDL cannot parameterize identifiers/password grammar. No SQL is logged.
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        await command.ExecuteNonQueryAsync();
    }

    [LocalPostgresFact]
    public async Task REV_01_EmptyProfileHasDurableInitialRevision()
    { await using var w = await Create(); var r = await w.Read(); Assert.Empty(r.Snapshot!.Selections); Assert.Empty(r.Snapshot.Goals); Assert.Equal(0, Revision(r).Version); Assert.NotEqual(Guid.Empty, Revision(r).GenerationId); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task REV_02_FirstGoalAdvancesRevision()
    { await using var w = await Create(); var r = await w.Read(); await w.Goal(); var next = await w.Read(); Assert.Single(next.Snapshot!.Goals); Assert.Equal(Revision(r).Version + 1, Revision(next).Version); }

    [LocalPostgresFact]
    public async Task REV_03_SelectionBatchAdvancesOnceAtomically()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill, w.Authority.SecondSkill)); var next = await w.Read(); Assert.Equal(2, next.Snapshot!.Selections.Count); Assert.Equal(1, Revision(next).Version); }

    [LocalPostgresFact]
    public async Task REV_04_UnrelatedAccountTransactionDoesNotInvalidateRevision()
    { await using var w = await Create(); var r = await w.Read(); var other = new LearningSetupScope(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(w.Fixture, other); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); }

    [LocalPostgresFact]
    public async Task REV_05_OtherLearningProfileChangesDoNotInvalidateRevision()
    { await using var w = await Create(); var r = await w.Read(); var other = new LearningSetupScope(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(w.Fixture, other); await w.Sql($"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES({Guid.NewGuid()},{other.LearningProfileId},'learning','OTHER-PRIVATE-GOAL')"); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); }

    [LocalPostgresFact]
    public async Task REV_06_HobbiesAndSocialDoNotAcquireLearningAnchors()
    { await using var w = await Create(); var r = await w.Read(); foreach (var module in new[] { "hobbies", "social" }) await LearningPostgresFixture.Profile(w.Fixture, new(Guid.NewGuid(), Guid.NewGuid()), module: module); await using var db = w.Open(); Assert.Equal(1, await db.Set<LearningProfileRevisionRow>().CountAsync()); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task REV_07_StaleRevisionCannotMutate()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); var current = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Conflict, await Change(w, r, w.Authority.SecondSkill)); Assert.Equal(current.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task REV_08_ConcurrentSameRevisionHasExactlyOneCommit()
    { await using var w = await Create(); var r = await w.Read(); var results = await Task.WhenAll(Change(w, r, w.Authority.Skill), Change(w, r, w.Authority.SecondSkill)); Assert.Single(results, x => x == LearningSetupWriteOutcome.Committed); Assert.Single(results, x => x == LearningSetupWriteOutcome.Conflict); Assert.Equal(1, Revision(await w.Read()).Version); }

    [LocalPostgresFact]
    public async Task REV_09_IndependentProfilesCanBothCommit()
    { await using var a = await Create(); var b = new Authority(); await LearningPostgresFixture.Profile(a.Fixture, b.Scope, b.UserId); var repo = new IsolatedLearningSetupRepository(a.Options(), b, b.Boundary, b); var left = await a.Read(); var right = await repo.ReadAsync(b.Scope); var results = await Task.WhenAll(Change(a, left, a.Authority.Skill), repo.ReplaceAsync(Select(right.Snapshot!, b.Skill), right.StorageRevision!, b.CatalogueVersion)); Assert.All(results, x => Assert.Equal(LearningSetupWriteOutcome.Committed, x)); }

    [LocalPostgresFact]
    public async Task REV_10_ReturningToSameContentDoesNotReuseRevision()
    { await using var w = await Create(); await w.Goal("A"); var r = await w.Read(); await w.Sql($"UPDATE andril_learning.learning_personal_goals SET text='B' WHERE learning_profile_id={w.Scope.LearningProfileId}"); await w.Sql($"UPDATE andril_learning.learning_personal_goals SET text='A' WHERE learning_profile_id={w.Scope.LearningProfileId}"); var current = await w.Read(); Assert.Equal(r.Snapshot!.Goals.Single().Text, current.Snapshot!.Goals.Single().Text); Assert.Equal(Revision(r).Version + 2, Revision(current).Version); Assert.Equal(LearningSetupWriteOutcome.Conflict, await Change(w, r, w.Authority.Skill)); }

    [LocalPostgresFact]
    public async Task REV_11_EmptyPopulatedEmptyPreservesAnchorAndInvalidatesOldToken()
    { await using var w = await Create(); var empty = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, empty, w.Authority.Skill)); await w.Goal(); await w.Sql($"DELETE FROM andril_learning.learning_personal_goals WHERE learning_profile_id={w.Scope.LearningProfileId}; DELETE FROM andril_learning.learning_skill_selections WHERE learning_profile_id={w.Scope.LearningProfileId}"); var current = await w.Read(); Assert.Empty(current.Snapshot!.Goals); Assert.Empty(current.Snapshot.Selections); Assert.Equal(Revision(empty).GenerationId, Revision(current).GenerationId); Assert.True(Revision(current).Version > Revision(empty).Version); Assert.Equal(LearningSetupWriteOutcome.Conflict, await Change(w, empty, w.Authority.Skill)); }

    [LocalPostgresFact]
    public async Task REV_12_RecreationUsesNewGenerationAndHistoricalGenerationCannotBeReused()
    {
        await using var w = await Create(); var r = await w.Read(); var generation = Revision(r).GenerationId;
        await w.Sql($"DELETE FROM andril_identity.private_module_profiles WHERE id={w.Scope.LearningProfileId}");
        await LearningPostgresFixture.Profile(w.Fixture, w.Scope, w.Authority.UserId);
        var current = await w.Read(); Assert.NotEqual(generation, Revision(current).GenerationId);
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await Change(w, r, w.Authority.Skill));
        await using var db = w.Open();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_profile_revisions SET generation_id={generation} WHERE learning_profile_id={w.Scope.LearningProfileId}"));
        Assert.Equal("P2003", error.SqlState);
        Assert.Equal(2, await db.Set<LearningGenerationRow>().CountAsync());
    }

    [LocalPostgresFact]
    public async Task REV_13_ForeignAccountCannotReadOrMutate()
    { await using var w = await Create(); var r = await w.Read(); var foreign = new LearningSetupScope(Guid.NewGuid(), w.Scope.LearningProfileId); Assert.Equal(LearningSetupReadState.Denied, (await w.Repository().ReadAsync(foreign)).State); var snapshot = new LearningSetupSnapshot(foreign, [], []); Assert.Equal(LearningSetupWriteOutcome.Denied, await w.Repository().ReplaceAsync(snapshot, r.StorageRevision!, w.Authority.CatalogueVersion)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task REV_14_ForeignModuleCannotAccessLearning()
    { await using var w = await Create(); var r = await w.Read(); w.Authority.Module = "social"; Assert.Equal(LearningSetupReadState.Denied, (await w.Repository().ReadAsync(w.Scope)).State); Assert.Equal(LearningSetupWriteOutcome.Denied, await Change(w, r, w.Authority.Skill)); }

    [LocalPostgresFact]
    public async Task REV_15_InactiveAndRevokedProfilesDenyWithoutPurgingData()
    {
        await using var w = await Create(); await w.Goal(); var r = await w.Read();
        await w.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={w.Scope.AccountId} AND module='learning'");
        Assert.Equal(LearningSetupReadState.Denied, (await w.Repository().ReadAsync(w.Scope)).State);
        Assert.Equal(LearningSetupWriteOutcome.Denied, await Change(w, r, w.Authority.Skill));
        await using (var db = w.Open()) Assert.Single(await db.PersonalGoals.ToListAsync());
        await w.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1 WHERE account_id={w.Scope.AccountId} AND module='learning'");
        Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision);
        w.Authority.Revoked = true; Assert.Equal(LearningSetupWriteOutcome.Denied, await Change(w, r, w.Authority.Skill));
    }

    [LocalPostgresFact]
    public async Task REV_16_MissingAnchorFailsClosedWithoutLazyRepair()
    { await using var w = await Create(); var r = await w.Read(); await w.Sql($"DELETE FROM andril_learning.learning_profile_revisions WHERE learning_profile_id={w.Scope.LearningProfileId}"); Assert.Equal(LearningSetupReadState.Unavailable, (await w.Repository().ReadAsync(w.Scope)).State); Assert.Equal(LearningSetupWriteOutcome.Unavailable, await Change(w, r, w.Authority.Skill)); await using var db = w.Open(); Assert.Empty(await db.Set<LearningProfileRevisionRow>().ToListAsync()); }

    [LocalPostgresFact]
    public async Task REV_17_CancellationAfterActualPreparationRollsBackDataAndRevision()
    {
        await using var w = await Create(); var r = await w.Read(); using var cts = new CancellationTokenSource(); var reached = false;
        var repo = w.Repository((phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ReplaceAsync(Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion, cts.Token));
        Assert.True(reached); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Empty((await w.Read()).Snapshot!.Selections);
    }

    [LocalPostgresFact]
    public async Task REV_18_PartialPersistenceFailureRollsBackEverything()
    { await using var w = await Create(); var r = await w.Read(); var repo = w.Repository((phase, _) => phase == "prepared" ? Task.FromException(new InvalidOperationException("SYNTHETIC-STAGED-FAILURE")) : Task.CompletedTask); await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ReplaceAsync(Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Empty((await w.Read()).Snapshot!.Selections); }

    [LocalPostgresFact]
    public async Task REV_19_RevisionWriteFailureRollsBackSelectionWrite()
    {
        await using var w = await Create(); var r = await w.Read(); await using (var db = w.Open())
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION andril_learning.fixture_revision_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION USING ERRCODE='P2099',MESSAGE='SYNTHETIC-REVISION-FAILURE'; END $$; CREATE TRIGGER fixture_revision_failure BEFORE UPDATE ON andril_learning.learning_profile_revisions FOR EACH ROW EXECUTE FUNCTION andril_learning.fixture_revision_failure()");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Change(w, r, w.Authority.Skill)); Assert.Equal("P2099", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Empty((await w.Read()).Snapshot!.Selections);
    }

    [LocalPostgresFact]
    public async Task REV_20_SerializationConflictIsNotRetried()
    {
        await using var w = await Create(); await using var db = w.Open(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var store = new IsolatedLearningPreferencesStore(db); var r = await store.ReadUnderTransactionAsync(w.Scope);
        await w.Goal();
        Assert.Equal(IsolatedLearningPreferencesStore.ReplacementState.Conflict, await store.PrepareReplacementAsync(Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, (await w.Authority.ReadCurrentAsync()).Catalogue!));
        await tx.RollbackAsync(); Assert.Empty((await w.Read()).Snapshot!.Selections); Assert.Equal(1, Revision(await w.Read()).Version);
    }

    [LocalPostgresFact]
    public async Task REV_21_FreshConnectionSeesPersistedRevision()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); await using var first = w.Open(); await using var second = w.Open(); await first.Database.OpenConnectionAsync(); await second.Database.OpenConnectionAsync(); Assert.NotEqual(((NpgsqlConnection)first.Database.GetDbConnection()).ProcessID, ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID); await using var tx = await second.Database.BeginTransactionAsync(IsolationLevel.Serializable); var persisted = await new IsolatedLearningPreferencesStore(second).ReadUnderTransactionAsync(w.Scope); Assert.Equal(1, Revision(persisted).Version); Assert.Equal((await w.Read()).StorageRevision, persisted.StorageRevision); await tx.CommitAsync(); }

    private sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            LearningDbContext.ConfigureLearningModel(model);
            model.Ignore<LearningMutationReceiptRow>(); model.Ignore<LearningProfileRevisionRow>();
            model.Ignore<LearningGenerationRow>(); model.Ignore<LearningProfileKeyRow>();
            model.Ignore<LearningCvRow>(); // The historical fixture intentionally contains only the original two data tables.
            model.Ignore<LearningPortfolioRow>();
        }
    }

    [LocalPostgresFact]
    public async Task REV_22_BackfillPreservesExistingSelectionsAndGoalsWithoutInventedHistory()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var scope = new LearningSetupScope(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(fixture, scope);
        await using (var db = LearningPostgresFixture.Open(fixture))
        {
            await using var legacy = new LegacyContext(new DbContextOptionsBuilder<LegacyContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            await legacy.Database.ExecuteSqlRawAsync(legacy.Database.GenerateCreateScript());
            var p = new LearningPreferences(scope.AccountId, scope.LearningProfileId); var skill = Guid.NewGuid(); p.UpdateSelections([], [skill], []); var goal = p.CreateGoal("SYNTHETIC-EXISTING-GOAL");
            legacy.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Teaching)); legacy.Add(LearningPersonalGoal.CreateFromPreferences(p, goal.Id)); await legacy.SaveChangesAsync();
        }
        await LearningPostgresFixture.Migrate(fixture);
        await using var check = LearningPostgresFixture.Open(fixture); Assert.Single(await check.SkillSelections.ToListAsync()); Assert.Equal("SYNTHETIC-EXISTING-GOAL", Assert.Single(await check.PersonalGoals.ToListAsync()).Text);
        var revision = Assert.Single(await check.Set<LearningProfileRevisionRow>().ToListAsync()); Assert.Equal(scope.LearningProfileId, revision.LearningProfileId); Assert.Equal(0, revision.Version);
        Assert.Equal(new[] { "20261125100000_LearningDurableRevisions", "20261126090000_LearningCvPersistence", "20261126110000_LearningPortfolioPersistence" }, await check.Database.GetAppliedMigrationsAsync());
    }

    [LocalPostgresFact]
    public async Task REV_23_EmptyBackfillIsLearningOnlyAndRepeatMigrationPreservesGenerations()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync();
        foreach (var module in new[] { "learning", "learning", "hobbies", "social" }) await LearningPostgresFixture.Profile(fixture, new(Guid.NewGuid(), Guid.NewGuid()), module: module);
        await LearningPostgresFixture.Migrate(fixture); await using var db = LearningPostgresFixture.Open(fixture);
        var before = await db.Set<LearningProfileRevisionRow>().AsNoTracking().OrderBy(r => r.LearningProfileId).ToListAsync(); Assert.Equal(2, before.Count); Assert.All(before, r => { Assert.Equal("learning", r.Module); Assert.Equal(0, r.Version); }); Assert.Equal(2, before.Select(r => r.GenerationId).Distinct().Count());
        await db.Database.MigrateAsync(); var after = await db.Set<LearningProfileRevisionRow>().AsNoTracking().OrderBy(r => r.LearningProfileId).ToListAsync(); Assert.Equal(before.Select(r => r.GenerationId), after.Select(r => r.GenerationId));
    }

    [LocalPostgresFact]
    public async Task REV_24_RestrictedRuntimeHasOwnerRlsWithoutGoalOrRevisionUpdatePrivileges()
    {
        await using var w = await Create(); await w.Goal(); var other = new LearningSetupScope(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(w.Fixture, other);
        var role = "andril_learning_runtime_" + Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using var owner = w.Open();
        await FixtureDdl(owner, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS");
        try
        {
            await FixtureDdl(owner, $"GRANT USAGE ON SCHEMA andril_learning TO {role}; GRANT SELECT ON andril_learning.learning_skill_selections,andril_learning.learning_personal_goals,andril_learning.learning_profile_revisions,andril_learning.learning_mutation_receipts TO {role}; GRANT INSERT,DELETE ON andril_learning.learning_skill_selections TO {role}; GRANT INSERT ON andril_learning.learning_mutation_receipts TO {role}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_profile_revision(uuid,uuid),andril_learning.lock_learning_owners(uuid,uuid,uuid) TO {role}");
            var settings = new NpgsqlConnectionStringBuilder(owner.Database.GetConnectionString()) { Username = role, Password = password, Pooling = false, IncludeErrorDetail = false };
            var options = new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(settings.ConnectionString).Options;
            var repo = new IsolatedLearningSetupRepository(options, w.Authority, w.Authority.Boundary, w.Authority);
            var r = await repo.ReadAsync(w.Scope); Assert.Equal(LearningSetupReadState.Available, r.State); Assert.Single(r.Snapshot!.Goals);
            Assert.Equal(LearningSetupWriteOutcome.Committed, await repo.ReplaceAsync(Select(r.Snapshot, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion));
            await using var runtime = new LearningDbContext(options); await runtime.Database.OpenConnectionAsync();
            await runtime.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{w.Authority.UserId!.Value.ToString()},false),set_config('andril.account',{w.Scope.AccountId.ToString()},false),set_config('andril.module','learning',false)");
            Assert.Empty(await runtime.PersonalGoals.Where(g => g.LearningProfileId == other.LearningProfileId).ToListAsync());
            foreach (var sql in new[] { "UPDATE andril_learning.learning_personal_goals SET text='forbidden'", "UPDATE andril_learning.learning_profile_revisions SET version=0", "SELECT * FROM andril_identity.accounts", "CREATE TABLE andril_learning.forbidden(id integer)" })
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(sql))).SqlState);
        }
        finally
        {
            await FixtureDdl(owner, $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}");
        }
    }

    [LocalPostgresFact]
    public async Task REV_25_ProfileDeletionCannotRacePastCoordinatedMutation()
    {
        await using var w = await Create(); var r = await w.Read();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = w.Repository(async (phase, ct) => { if (phase == "before-commit") { prepared.SetResult(); await release.Task.WaitAsync(ct); } });
        var mutation = repo.ReplaceAsync(Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion);
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var deletion = w.Open(); await deletion.Database.OpenConnectionAsync(); var pid = ((NpgsqlConnection)deletion.Database.GetDbConnection()).ProcessID;
        var deleting = deletion.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_identity.private_module_profiles WHERE id={w.Scope.LearningProfileId}");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5); bool blocked;
            do { await using var probe = w.Open(); blocked = await probe.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid}))>0 AS \"Value\"").SingleAsync(); if (!blocked) await Task.Delay(20); } while (!blocked && DateTime.UtcNow < deadline);
            Assert.True(blocked); Assert.False(deleting.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(LearningSetupWriteOutcome.Committed, await mutation); await deleting;
        Assert.Equal(LearningSetupReadState.Denied, (await w.Repository().ReadAsync(w.Scope)).State);
        await using var db = w.Open(); Assert.Empty(await db.Set<LearningProfileRevisionRow>().ToListAsync()); Assert.Empty(await db.SkillSelections.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task REV_26_DataAndRevisionShareOneConsistentSnapshot()
    {
        await using var w = await Create(); await using var db = w.Open(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var store = new IsolatedLearningPreferencesStore(db); var before = await store.ReadUnderTransactionAsync(w.Scope); await w.Goal(); var repeat = await store.ReadUnderTransactionAsync(w.Scope);
        Assert.Equal(before.StorageRevision, repeat.StorageRevision); Assert.Empty(repeat.Snapshot!.Goals); await tx.CommitAsync(); Assert.Equal(1, Revision(await w.Read()).Version); Assert.Single((await w.Read()).Snapshot!.Goals);
    }

    [LocalPostgresFact]
    public async Task REV_27_MaximumVersionDoesNotWrapOrPartiallyCommit()
    { await using var w = await Create(); await w.Sql($"UPDATE andril_learning.learning_profile_revisions SET version={long.MaxValue} WHERE learning_profile_id={w.Scope.LearningProfileId}"); var r = await w.Read(); Assert.Equal(long.MaxValue, Revision(r).Version); Assert.Equal(LearningSetupWriteOutcome.Unavailable, await Change(w, r, w.Authority.Skill)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Empty((await w.Read()).Snapshot!.Selections); }

    [LocalPostgresFact]
    public async Task REV_28_LegacySnapshotTokensAreRejected()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Conflict, await w.Repository().ReplaceAsync(Select(r.Snapshot!, w.Authority.Skill), new string('A', 64), w.Authority.CatalogueVersion)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task FD03_01_MatchingUnchangedContentSucceedsWithoutRevisionAdvance()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r)); var current = await w.Read(); Assert.Equal(r.StorageRevision, current.StorageRevision); await using var db = w.Open(); var receipt = Assert.Single(await db.Set<LearningMutationReceiptRow>().ToListAsync()); Assert.Equal(receipt.ExpectedVersion, receipt.ResultVersion); }

    [LocalPostgresFact]
    public async Task FD03_02_MatchingChangedContentPreservesGoalAndSelectionIdentities()
    { await using var w = await Create(); await w.Goal(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); var first = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, first, w.Authority.Skill, w.Authority.SecondSkill)); var current = await w.Read(); Assert.Equal(first.Snapshot!.Selections.Single().Id, current.Snapshot!.Selections.Single(s => s.SkillId == w.Authority.Skill).Id); Assert.Equal(r.Snapshot!.Goals.Single().Id, current.Snapshot.Goals.Single().Id); Assert.Equal(Revision(first).Version + 1, Revision(current).Version); }

    [LocalPostgresFact]
    public async Task FD03_03_StaleUnchangedContentIsStillConflict()
    { await using var w = await Create(); var r = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, r, w.Authority.Skill)); var current = await w.Read(); Assert.Equal(LearningSetupWriteOutcome.Conflict, await w.Repository().ReplaceAsync(current.Snapshot!, r.StorageRevision!, w.Authority.CatalogueVersion)); Assert.Equal(current.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task FD03_04_StaleChangedContentIsConflict()
    { await using var w = await Create(); var r = await w.Read(); await w.Goal(); Assert.Equal(LearningSetupWriteOutcome.Conflict, await Change(w, r, w.Authority.Skill)); Assert.Empty((await w.Read()).Snapshot!.Selections); }

    [LocalPostgresFact]
    public async Task FD03_05_WrongGenerationCannotUseUnchangedContent()
    { await using var w = await Create(); var r = await w.Read(); var wrong = LearningSetupRevision.FromPersisted(Guid.NewGuid(), Revision(r).Version).ToOpaqueToken(); Assert.Equal(LearningSetupWriteOutcome.Conflict, await w.Repository().ReplaceAsync(r.Snapshot!, wrong, w.Authority.CatalogueVersion)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task FD03_06_InactiveProfileCannotUseNoOpSuccess()
    { await using var w = await Create(); var r = await w.Read(); await w.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={w.Scope.AccountId} AND module='learning'"); Assert.Equal(LearningSetupWriteOutcome.Denied, await Change(w, r)); }

    [LocalPostgresFact]
    public async Task FD03_07_UnauthorizedProfileCannotUseNoOpSuccess()
    { await using var w = await Create(); var r = await w.Read(); w.Authority.Standing = false; Assert.Equal(LearningSetupWriteOutcome.Denied, await Change(w, r)); }

    [LocalPostgresFact]
    public async Task FD03_08_ConcurrentNoOpsBothSucceedWithoutAdvancingRevision()
    { await using var w = await Create(); var r = await w.Read(); var results = await Task.WhenAll(Change(w, r), Change(w, r)); Assert.All(results, x => Assert.Equal(LearningSetupWriteOutcome.Committed, x)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("SYNTHETIC-LOST-COMMIT-ACKNOWLEDGEMENT"));
    }

    [LocalPostgresFact]
    public async Task CommitUnknownReconcilesActualCommittedReceiptWithoutMutationReplay()
    {
        await using var w = await Create(); var r = await w.Read(); var operation = Guid.NewGuid();
        var repo = w.Repository(null, new LostCommitAcknowledgement());
        Assert.Equal(LearningSetupWriteOutcome.CommitUnknown, await repo.ReplaceWithOperationAsync(operation, Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion));
        Assert.Equal(IsolatedLearningSetupRepository.Reconciliation.ConfirmedCommitted, await w.Repository().ReconcileAsync(w.Scope, operation, r.StorageRevision!));
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await w.Repository().ReplaceWithOperationAsync(operation, Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, w.Authority.CatalogueVersion));
        Assert.Equal(1, Revision(await w.Read()).Version);
    }

    [LocalPostgresFact]
    public async Task UnavailableAuthorityFailsClosedWithoutDisclosingOrMutatingProtectedState()
    {
        await using var w = await Create();
        await w.Goal();
        var before = await w.Read();
        Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, before, w.Authority.Skill));
        before = await w.Read();
        var originalSelections = before.Snapshot!.Selections.Select(s => (s.Id, s.SkillId, s.Kind)).ToArray();
        var originalGoals = before.Snapshot.Goals.Select(g => (g.Id, g.Text)).ToArray();

        w.Authority.Unavailable = true;
        var deniedRead = await w.Repository().ReadAsync(w.Scope);
        Assert.Equal(LearningSetupReadState.Unavailable, deniedRead.State);
        Assert.Null(deniedRead.Snapshot);
        Assert.Null(deniedRead.StorageRevision);
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await Change(w, before, w.Authority.SecondSkill));

        // Inspect actual committed rows independently of the unavailable application authority.
        await using (var db = w.Open())
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var persisted = await new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(w.Scope);
            Assert.Equal(LearningSetupReadState.Available, persisted.State);
            Assert.Equal(before.StorageRevision, persisted.StorageRevision);
            Assert.Equal(originalSelections, persisted.Snapshot!.Selections.Select(s => (s.Id, s.SkillId, s.Kind)));
            Assert.Equal(originalGoals, persisted.Snapshot.Goals.Select(g => (g.Id, g.Text)));
            await tx.CommitAsync();
        }
        w.Authority.Unavailable = false;
        Assert.Equal(before.StorageRevision, (await w.Read()).StorageRevision);
    }

    [LocalPostgresFact]
    public async Task NoOpCommitUnknownReconcilesReceiptWithoutChangingRevision()
    { await using var w = await Create(); var r = await w.Read(); var operation = Guid.NewGuid(); Assert.Equal(LearningSetupWriteOutcome.CommitUnknown, await w.Repository(null, new LostCommitAcknowledgement()).ReplaceWithOperationAsync(operation, r.Snapshot!, r.StorageRevision!, w.Authority.CatalogueVersion)); Assert.Equal(IsolatedLearningSetupRepository.Reconciliation.ConfirmedCommitted, await w.Repository().ReconcileAsync(w.Scope, operation, r.StorageRevision!)); Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); }

    [LocalPostgresFact]
    public async Task ReconciliationDistinguishesNonCommitAndUnresolvedAfterOtherMutation()
    { await using var w = await Create(); var r = await w.Read(); var operation = Guid.NewGuid(); Assert.Equal(IsolatedLearningSetupRepository.Reconciliation.ConfirmedNotCommitted, await w.Repository().ReconcileAsync(w.Scope, operation, r.StorageRevision!)); await w.Goal(); Assert.Equal(IsolatedLearningSetupRepository.Reconciliation.Unresolved, await w.Repository().ReconcileAsync(w.Scope, operation, r.StorageRevision!)); w.Authority.Revoked = true; Assert.Equal(IsolatedLearningSetupRepository.Reconciliation.Unresolved, await w.Repository().ReconcileAsync(w.Scope, operation, r.StorageRevision!)); }

    [LocalPostgresFact]
    public async Task ReadReleaseAndCommitRecheckCurrentAuthorityAndCatalogue()
    {
        await using var w = await Create(); var r = await w.Read();
        var reader = w.Repository((phase, _) => { if (phase == "read-release") w.Authority.Revoked = true; return Task.CompletedTask; });
        Assert.Equal(LearningSetupReadState.Denied, (await reader.ReadAsync(w.Scope)).State); w.Authority.Revoked = false;
        var writer = w.Repository((phase, _) => { if (phase == "before-commit") w.Authority.CatalogueVersion = "catalogue-v2"; return Task.CompletedTask; });
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await writer.ReplaceAsync(Select(r.Snapshot!, w.Authority.Skill), r.StorageRevision!, "catalogue-v1"));
        Assert.Equal(r.StorageRevision, (await w.Read()).StorageRevision); Assert.Empty((await w.Read()).Snapshot!.Selections);
    }

    [LocalPostgresFact]
    public async Task ControlledGoalCommandsCreateEditNoOpAndRemoveAtomicallyWithoutChangingSelections()
    {
        await using var w = await Create(); var initial = await w.Read();
        Assert.Equal(LearningSetupWriteOutcome.Committed, await Change(w, initial, w.Authority.Skill));
        var selected = await w.Read(); var selection = Assert.Single(selected.Snapshot!.Selections);
        var service = new LearningPreferencesApplicationService(w.Authority.Boundary, w.Repository(), w.Authority);
        var create = LearningGoalChange.Create("SYNTHETIC-GOAL-A");
        Assert.Equal(LearningSetupWriteOutcome.Committed, await service.ChangeGoalAsync(Guid.NewGuid(), create, selected.StorageRevision!));
        var added = await w.Read(); Assert.Equal(create.GoalId, Assert.Single(added.Snapshot!.Goals).Id);
        Assert.Equal(Revision(selected).Version + 1, Revision(added).Version);
        Assert.Equal(LearningSetupWriteOutcome.Committed, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(create.GoalId, "SYNTHETIC-GOAL-A"), added.StorageRevision!));
        Assert.Equal(added.StorageRevision, (await w.Read()).StorageRevision);
        Assert.Equal(LearningSetupWriteOutcome.Committed, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(create.GoalId, "SYNTHETIC-GOAL-B"), added.StorageRevision!));
        var edited = await w.Read(); Assert.Equal("SYNTHETIC-GOAL-B", Assert.Single(edited.Snapshot!.Goals).Text);
        Assert.Equal(LearningSetupWriteOutcome.Committed, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Remove(create.GoalId), edited.StorageRevision!));
        var removed = await w.Read(); Assert.Empty(removed.Snapshot!.Goals); Assert.Equal(selection.Id, Assert.Single(removed.Snapshot.Selections).Id);
        Assert.Equal(Revision(edited).Version + 1, Revision(removed).Version);
    }

    [LocalPostgresFact]
    public async Task ControlledGoalCommandsRejectStaleUnknownAndRevokedOperationsWithoutMutation()
    {
        await using var w = await Create(); await w.Goal(); var before = await w.Read(); var goal = Assert.Single(before.Snapshot!.Goals);
        var service = new LearningPreferencesApplicationService(w.Authority.Boundary, w.Repository(), w.Authority);
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(Guid.NewGuid(), "changed"), before.StorageRevision!));
        Assert.Equal(LearningSetupWriteOutcome.Committed, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(goal.Id, "changed"), before.StorageRevision!));
        var current = await w.Read();
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(goal.Id, "changed"), before.StorageRevision!));
        w.Authority.Revoked = true;
        Assert.Equal(LearningSetupWriteOutcome.Denied, await service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Remove(goal.Id), current.StorageRevision!));
        w.Authority.Revoked = false; Assert.Equal(current.StorageRevision, (await w.Read()).StorageRevision); Assert.Single((await w.Read()).Snapshot!.Goals);
    }

    [LocalPostgresFact]
    public async Task ControlledGoalCancellationAfterPreparationRollsBackGoalAndRevision()
    {
        await using var w = await Create(); await w.Goal(); var before = await w.Read(); using var cts = new CancellationTokenSource(); bool reached = false;
        var repository = w.Repository((phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        var service = new LearningPreferencesApplicationService(w.Authority.Boundary, repository, w.Authority);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Remove(before.Snapshot!.Goals.Single().Id), before.StorageRevision!, cts.Token));
        Assert.True(reached); Assert.Equal(before.StorageRevision, (await w.Read()).StorageRevision); Assert.Single((await w.Read()).Snapshot!.Goals);
    }

    [LocalPostgresFact]
    public async Task ControlledGoalConcurrencyHasOneCommitAndOneConflict()
    {
        await using var w = await Create(); await w.Goal(); var before = await w.Read(); var id = before.Snapshot!.Goals.Single().Id;
        var service = new LearningPreferencesApplicationService(w.Authority.Boundary, w.Repository(), w.Authority);
        var results = await Task.WhenAll(service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(id, "left"), before.StorageRevision!),
            service.ChangeGoalAsync(Guid.NewGuid(), LearningGoalChange.Edit(id, "right"), before.StorageRevision!));
        Assert.Single(results, result => result == LearningSetupWriteOutcome.Committed); Assert.Single(results, result => result == LearningSetupWriteOutcome.Conflict);
        Assert.Equal(Revision(before).Version + 1, Revision(await w.Read()).Version);
    }

    [LocalPostgresFact]
    public async Task LearningMigrationRollsBackReappliesWithoutChangingAccountsHistory()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); await LearningPostgresFixture.Profile(fixture, new(Guid.NewGuid(), Guid.NewGuid()));
        await using var accounts = fixture.Open(); var accountHistory = (await accounts.Database.GetAppliedMigrationsAsync()).ToArray();
        await LearningPostgresFixture.Migrate(fixture); await using var db = LearningPostgresFixture.Open(fixture); Assert.False(db.Database.HasPendingModelChanges());
        await db.GetService<IMigrator>().MigrateAsync("0"); Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); Assert.Single(await db.Set<LearningProfileRevisionRow>().ToListAsync()); Assert.Equal(accountHistory, (await accounts.Database.GetAppliedMigrationsAsync()).ToArray());
    }
}
