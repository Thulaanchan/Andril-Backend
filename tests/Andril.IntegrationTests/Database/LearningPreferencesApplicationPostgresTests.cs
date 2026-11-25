using System.Data;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>
/// Real service/domain/EF composition with isolated synthetic backend authority only.
/// The private test fence coordinates synthetic authority/catalogue and transactions; it is
/// NOT a new M2 production mechanism. No production adapter, DI, HTTP or migration is supplied.
/// Existing LocalAccountDatabase opt-in, loopback validation and cleanup are reused.
/// </summary>
public sealed class LearningPreferencesApplicationPostgresTests
{
    private sealed class Composition(LocalAccountDatabase fixture) : ICurrentUser,
        ILearningSetupAuthorityReader, ILearningSetupRepository, ILearningSkillCatalogueReader
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        internal readonly Guid Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Skill = Guid.NewGuid(), Version = Guid.NewGuid();
        private readonly SemaphoreSlim fence = new(1, 1);
        internal string Defect = "", CatalogueRevision = "catalogue-v1";
        internal bool AuthorityUnavailable, CatalogueUnavailable, StorageUnavailable, Revoked;
        internal Action? BeforeReadRelease, AfterCatalogueRead, BeforeCommit;
        internal Func<Task>? AfterRead;
        internal int Committed;
        internal LearningSkillCatalogue? Catalogue;
        internal LearningSetupScope Scope => new(Account, Profile);
        private LearningSetupAccessBoundary Boundary => new(this, this);
        internal LearningPreferencesApplicationService Service => new(Boundary, this, this);
        internal LearningDbContext Open()
        {
            using var accounts = fixture.Open();
            return new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(accounts.Database.GetConnectionString()).Options);
        }
        internal async Task Initialize()
        {
            Catalogue = new([new(Skill, true, LearningSkillCatalogueStatus.Active)]);
            await LearningPostgresFixture.Migrate(fixture);
            await LearningPostgresFixture.Profile(fixture, Scope, UserId);
            await using var db = Open();
            var p = new LearningPreferences(Account, Profile); p.UpdateSelections([], [Skill], []);
            var goal = p.CreateGoal("SYNTHETIC-PRIVATE-APPLICATION-GOAL");
            db.SkillSelections.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, Skill, LearningSkillSelectionKind.Teaching));
            db.PersonalGoals.Add(LearningPersonalGoal.CreateFromPreferences(p, goal.Id)); await db.SaveChangesAsync();
        }
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LearningSetupAccessEvidence evidence = new(subject, Account, Profile, Account, "learning", operation,
                true, true, true, true, true, Revoked, false, Version, Version);
            evidence = Defect switch
            {
                "owner" => evidence with { ProfileOwnerAccountId = Guid.NewGuid() },
                "module" => evidence with { Module = "social" },
                "inactive" => evidence with { MembershipActive = false },
                "stale" => evidence with { CurrentVersion = Guid.NewGuid() }, _ => evidence
            };
            return Task.FromResult<LearningSetupAccessEvidence?>(AuthorityUnavailable ? null : evidence);
        }
        private async Task<LearningSetupAccessState> Check(LearningSetupScope scope, LearningSetupOperation operation, CancellationToken ct)
        {
            var result = await Boundary.CheckAsync(operation, ct);
            if (result.State != LearningSetupAccessState.Allowed) return result.State;
            return result.Scope!.AccountId == scope.AccountId && result.Scope.LearningProfileId == scope.LearningProfileId
                && result.Scope.Module == scope.Module ? LearningSetupAccessState.Allowed : LearningSetupAccessState.Denied;
        }
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = CatalogueUnavailable ? LearningSkillCatalogueReadResult.Unavailable()
                : LearningSkillCatalogueReadResult.Available(Catalogue!, CatalogueRevision);
            AfterCatalogueRead?.Invoke(); return Task.FromResult(result);
        }
        public async Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
        {
            await fence.WaitAsync(cancellationToken);
            try
            {
                var access = await Check(scope, LearningSetupOperation.ReadSetup, cancellationToken);
                if (access == LearningSetupAccessState.Denied) return LearningSetupReadResult.Denied();
                if (access != LearningSetupAccessState.Allowed || StorageUnavailable) return LearningSetupReadResult.Unavailable();
                LearningSetupReadResult read;
                await using (var db = Open())
                await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
                {
                    read = await new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(scope, cancellationToken);
                    BeforeReadRelease?.Invoke();
                    access = await Check(scope, LearningSetupOperation.ReadSetup, cancellationToken);
                    if (access == LearningSetupAccessState.Denied) return LearningSetupReadResult.Denied();
                    if (access != LearningSetupAccessState.Allowed) return LearningSetupReadResult.Unavailable();
                    await tx.CommitAsync(cancellationToken);
                }
                if (AfterRead is not null) await AfterRead();
                return read;
            }
            finally { fence.Release(); }
        }
        public async Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision,
            string catalogueRevision, CancellationToken cancellationToken = default)
        {
            await fence.WaitAsync(cancellationToken);
            try
            {
                var access = await Check(replacement.Scope, LearningSetupOperation.UpdateSetup, cancellationToken);
                if (access == LearningSetupAccessState.Denied) return LearningSetupWriteOutcome.Denied;
                if (access != LearningSetupAccessState.Allowed || StorageUnavailable || CatalogueUnavailable)
                    return LearningSetupWriteOutcome.Unavailable;
                if (catalogueRevision != CatalogueRevision) return LearningSetupWriteOutcome.Conflict;
                await using var db = Open();
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                var store = new IsolatedLearningPreferencesStore(db);
                var prepared = await store.PrepareReplacementAsync(replacement, expectedStorageRevision, Catalogue!, cancellationToken);
                if (prepared == IsolatedLearningPreferencesStore.ReplacementState.Conflict) return LearningSetupWriteOutcome.Conflict;
                if (prepared == IsolatedLearningPreferencesStore.ReplacementState.Unavailable) return LearningSetupWriteOutcome.Unavailable;
                BeforeCommit?.Invoke();
                access = await Check(replacement.Scope, LearningSetupOperation.UpdateSetup, cancellationToken);
                if (access == LearningSetupAccessState.Denied) return LearningSetupWriteOutcome.Denied;
                if (access != LearningSetupAccessState.Allowed || CatalogueUnavailable) return LearningSetupWriteOutcome.Unavailable;
                if (CatalogueRevision != catalogueRevision) return LearningSetupWriteOutcome.Conflict;
                Catalogue!.ValidateNewSelections(replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId),
                    replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Learning).Select(s => s.SkillId));
                var result = await store.CommitPreparedAsync(cancellationToken);
                if (result == LearningSetupWriteOutcome.Committed) Committed++;
                return result;
            }
            finally { fence.Release(); }
        }
        internal async Task AssertOriginalRows()
        {
            await using var db = Open();
            Assert.Equal(Skill, (await db.SkillSelections.SingleAsync()).SkillId);
            Assert.Equal("SYNTHETIC-PRIVATE-APPLICATION-GOAL", (await db.PersonalGoals.SingleAsync()).Text);
            Assert.Equal(0, Committed);
        }
    }

    [LocalPostgresFact]
    public async Task ExistingApplicationServiceReadsAndCommitsInitialSelectionsWithoutSetupCompletion()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        var before = await c.Service.ReadAsync(); Assert.Equal(LearningSetupReadState.Available, before.State);
        Assert.Equal(LearningSetupWriteOutcome.Committed, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        var after = await c.Service.ReadAsync(); Assert.Equal(2, after.Snapshot!.Selections.Count);
        Assert.Equal(before.Snapshot!.Selections[0].Id, after.Snapshot.Selections.Single(s => s.Kind == LearningSkillSelectionKind.Teaching).Id);
        Assert.Equal(before.Snapshot.Goals.Select(g => (g.Id, g.Text)), after.Snapshot.Goals.Select(g => (g.Id, g.Text)));
        Assert.NotEqual(before.StorageRevision, after.StorageRevision); Assert.Single(before.Snapshot.Selections); Assert.Equal(1, c.Committed);
    }

    [LocalPostgresFact]
    public async Task InvalidCatalogueSelectionsNeverMutateDatabase()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        await Assert.ThrowsAsync<ArgumentException>(() => c.Service.UpdateSelectionsAsync([Guid.NewGuid()], []));
        c.Catalogue = new([new(c.Skill, false, LearningSkillCatalogueStatus.Active)]);
        await Assert.ThrowsAsync<ArgumentException>(() => c.Service.UpdateSelectionsAsync([c.Skill], []));
        await c.AssertOriginalRows();
    }

    [LocalPostgresFact]
    public async Task ForeignInactiveModuleAndStaleAuthorityNeverReleaseOrMutatePrivateRows()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        foreach (var defect in new[] { "owner", "module", "inactive", "stale" })
        {
            c.Defect = defect; var result = await c.Service.ReadAsync();
            Assert.Equal(LearningSetupReadState.Denied, result.State); Assert.Null(result.Snapshot);
            Assert.Equal(LearningSetupWriteOutcome.Denied, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        }
        c.Defect = "";
        Assert.Equal(LearningSetupReadState.Denied, (await c.ReadAsync(new(Guid.NewGuid(), c.Profile))).State);
        Assert.Equal(LearningSetupReadState.Denied, (await c.ReadAsync(new(c.Account, Guid.NewGuid()))).State);
        await c.AssertOriginalRows();
    }

    [LocalPostgresFact]
    public async Task RealGoalCommitAfterApplicationReadCausesStaleStorageConflict()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        c.AfterRead = async () =>
        {
            await using var db = c.Open();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_personal_goals SET text='independent writer' WHERE learning_profile_id={c.Profile}");
            await tx.CommitAsync();
        };
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        await using var check = c.Open(); Assert.Single(await check.SkillSelections.ToArrayAsync());
        Assert.Equal("independent writer", (await check.PersonalGoals.SingleAsync()).Text); Assert.Equal(0, c.Committed);
    }

    [LocalPostgresFact]
    public async Task CatalogueRevisionChangesBeforePreparationAndAfterPreparationRejectAtomically()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        c.AfterCatalogueRead = () => c.CatalogueRevision = "catalogue-v2";
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        c.AfterCatalogueRead = null; c.BeforeCommit = () => c.CatalogueRevision = "catalogue-v3";
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        await c.AssertOriginalRows();
    }

    [LocalPostgresFact]
    public async Task RevocationAtReadReleaseAndAfterPreparationDeniesWithoutPersistingChanges()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        c.BeforeReadRelease = () => c.Revoked = true;
        var read = await c.Service.ReadAsync(); Assert.Equal(LearningSetupReadState.Denied, read.State); Assert.Null(read.Snapshot);
        c.BeforeReadRelease = null; c.Revoked = false; c.BeforeCommit = () => c.Revoked = true;
        Assert.Equal(LearningSetupWriteOutcome.Denied, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        await c.AssertOriginalRows();
    }

    [LocalPostgresFact]
    public async Task CancellationAfterActualPreparationPropagatesAndRollsBack()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        using var cts = new CancellationTokenSource(); c.BeforeCommit = cts.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill], cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken); await c.AssertOriginalRows();
    }

    [LocalPostgresFact]
    public async Task UnavailableAuthorityCatalogueOrStorageDoesNotFabricateDataOrSuccess()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync(); var c = new Composition(fixture); await c.Initialize();
        c.AuthorityUnavailable = true;
        Assert.Equal(LearningSetupReadState.Unavailable, (await c.Service.ReadAsync()).State);
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        c.AuthorityUnavailable = false; c.CatalogueUnavailable = true;
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        c.CatalogueUnavailable = false; c.StorageUnavailable = true;
        var read = await c.Service.ReadAsync(); Assert.Equal(LearningSetupReadState.Unavailable, read.State); Assert.Null(read.Snapshot);
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await c.Service.UpdateSelectionsAsync([c.Skill], [c.Skill]));
        await c.AssertOriginalRows();
    }
}
