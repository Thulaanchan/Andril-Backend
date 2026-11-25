using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPreferencesApplicationServiceTests
{
    private sealed class Fixture : ILearningSetupAuthorityReader, ICurrentUser, ILearningSetupRepository, ILearningSkillCatalogueReader
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        internal Guid Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Skill = Guid.NewGuid(), Version = Guid.NewGuid();
        internal int AuthCalls, Reads, Writes, CatalogueReads;
        internal Action? OnRead, OnCatalogue, OnWrite;
        internal Func<int, LearningSetupAccessEvidence, LearningSetupAccessEvidence?> Change = (_, evidence) => evidence;
        internal LearningSetupReadResult Stored;
        internal LearningSkillCatalogueReadResult Catalogue;
        internal LearningSetupSnapshot? Written;
        internal string? ExpectedRevision, CatalogueRevision;
        internal LearningSetupWriteOutcome Outcome = LearningSetupWriteOutcome.Committed;
        internal readonly List<LearningSetupOperation> Operations = [];
        internal LearningPreferencesApplicationService Service => new(new LearningSetupAccessBoundary(this, this), this, this);
        internal Fixture()
        {
            var p = new LearningPreferences(Account, Profile); p.UpdateSelections([], [Skill], []);
            var goal = p.CreateGoal("PRIVATE-GOAL");
            Stored = LearningSetupReadResult.Available(new(new(Account, Profile),
                [LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, Skill, LearningSkillSelectionKind.Teaching)],
                [LearningPersonalGoal.CreateFromPreferences(p, goal.Id)]), "storage-v1");
            Catalogue = LearningSkillCatalogueReadResult.Available(new([new(Skill, true, LearningSkillCatalogueStatus.Active)]), "catalogue-v1");
        }
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); AuthCalls++; Operations.Add(operation);
            return Task.FromResult(Change(AuthCalls, new(subject, Account, Profile, Account, "learning", operation,
                true, true, true, true, true, false, false, Version, Version)));
        }
        public Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; OnRead?.Invoke(); return Task.FromResult(Stored);
        }
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); CatalogueReads++; OnCatalogue?.Invoke(); return Task.FromResult(Catalogue);
        }
        public Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision,
            string catalogueRevision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++; Written = replacement;
            ExpectedRevision = expectedStorageRevision; CatalogueRevision = catalogueRevision;
            OnWrite?.Invoke(); return Task.FromResult(Outcome);
        }
        // This fake records calls only; it proves no PostgreSQL transaction, authorization
        // coordination, real revision freshness or rollback behavior.
    }

    [Fact]
    public async Task ReadUsesFreshReadPermissionBeforeAndAfterRepositoryWithoutRequiringCompletion()
    {
        var f = new Fixture(); var result = await f.Service.ReadAsync();
        Assert.Same(f.Stored, result); Assert.Equal(1, f.Reads); Assert.Equal(0, f.Writes);
        Assert.Equal(new[] { LearningSetupOperation.ReadSetup, LearningSetupOperation.ReadSetup }, f.Operations);
    }

    [Fact]
    public async Task UpdateReusesDomainCatalogueRulesAndPreservesGoalAndSelectionIdentities()
    {
        var f = new Fixture(); var before = f.Stored.Snapshot!;
        var result = await f.Service.UpdateSelectionsAsync([f.Skill], [f.Skill]);
        Assert.Equal(LearningSetupWriteOutcome.Committed, result); Assert.Equal(1, f.Writes);
        Assert.NotNull(f.Written); Assert.Equal(2, f.Written.Selections.Count);
        Assert.Equal(before.Selections[0].Id, Assert.Single(f.Written.Selections, row => row.Kind == LearningSkillSelectionKind.Teaching).Id);
        Assert.Same(before.Goals, f.Written.Goals); Assert.Equal("PRIVATE-GOAL", Assert.Single(f.Written.Goals).Text);
        Assert.Single(before.Selections); Assert.Equal("storage-v1", f.ExpectedRevision); Assert.Equal("catalogue-v1", f.CatalogueRevision);
        Assert.Equal(new[] { LearningSetupOperation.UpdateSetup, LearningSetupOperation.UpdateSetup }, f.Operations);
    }

    [Theory]
    [InlineData("owner", false)] [InlineData("module", false)] [InlineData("stale", false)] [InlineData("unavailable", false)]
    [InlineData("owner", true)] [InlineData("module", true)] [InlineData("stale", true)] [InlineData("unavailable", true)]
    public async Task FailedInitialAuthorizationNeverReadsOrMutatesRepository(string defect, bool update)
    {
        var f = new Fixture(); f.Change = (_, e) => defect switch
        {
            "owner" => e with { ProfileOwnerAccountId = Guid.NewGuid() }, "module" => e with { Module = "social" },
            "stale" => e with { CurrentVersion = Guid.NewGuid() }, _ => null
        };
        if (update) Assert.Equal(defect == "unavailable" ? LearningSetupWriteOutcome.Unavailable : LearningSetupWriteOutcome.Denied,
            await f.Service.UpdateSelectionsAsync([f.Skill], []));
        else Assert.Equal(defect == "unavailable" ? LearningSetupReadState.Unavailable : LearningSetupReadState.Denied,
            (await f.Service.ReadAsync()).State);
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ForeignRepositoryScopeIsDenied(bool foreignAccount)
    {
        var f = new Fixture(); f.Stored = LearningSetupReadResult.Available(new(new(
            foreignAccount ? Guid.NewGuid() : f.Account, foreignAccount ? f.Profile : Guid.NewGuid()), [], []), "revision");
        Assert.Equal(LearningSetupReadState.Denied, (await f.Service.ReadAsync()).State);
        Assert.Equal(LearningSetupWriteOutcome.Denied, await f.Service.UpdateSelectionsAsync([f.Skill], []));
        Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads);
    }

    [Theory]
    [InlineData("denied")] [InlineData("unavailable")]
    public async Task RepositoryReadFailuresHaveSafeOutcomes(string state)
    {
        var f = new Fixture { Stored = state == "denied" ? LearningSetupReadResult.Denied() : LearningSetupReadResult.Unavailable() };
        Assert.Equal(f.Stored.State, (await f.Service.ReadAsync()).State);
        Assert.Equal(state == "denied" ? LearningSetupWriteOutcome.Denied : LearningSetupWriteOutcome.Unavailable,
            await f.Service.UpdateSelectionsAsync([], []));
        Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("retired")] [InlineData("unapproved")] [InlineData("duplicate")] [InlineData("null")]
    public async Task InvalidSelectionsCannotReachWriteOrChangeExistingSnapshot(string defect)
    {
        var f = new Fixture(); var before = f.Stored.Snapshot;
        if (defect is "retired" or "unapproved") f.Catalogue = LearningSkillCatalogueReadResult.Available(new([
            new(f.Skill, defect != "unapproved", defect == "retired" ? LearningSkillCatalogueStatus.Retired : LearningSkillCatalogueStatus.Active)]), "revision");
        IEnumerable<Guid> teaching = defect switch
        { "unknown" => [Guid.NewGuid()], "duplicate" => [f.Skill, f.Skill], "null" => null!, _ => [f.Skill] };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Service.UpdateSelectionsAsync(teaching, []));
        Assert.Equal(0, f.Writes); Assert.Same(before, f.Stored.Snapshot);
        Assert.Single(f.Stored.Snapshot!.Selections); Assert.Equal("PRIVATE-GOAL", Assert.Single(f.Stored.Snapshot.Goals).Text);
    }

    [Fact]
    public async Task UnavailableCatalogueCannotBecomeSuccessfulEmptySelectionUpdate()
    {
        var f = new Fixture { Catalogue = LearningSkillCatalogueReadResult.Unavailable() };
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await f.Service.UpdateSelectionsAsync([], []));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("revoked", false)] [InlineData("stale", false)] [InlineData("scope", false)] [InlineData("unavailable", false)]
    [InlineData("revoked", true)] [InlineData("stale", true)] [InlineData("scope", true)] [InlineData("unavailable", true)]
    public async Task ChangedAuthorityAfterDependenciesPreventsReleaseOrMutation(string defect, bool update)
    {
        var f = new Fixture(); f.Change = (call, e) => call == 1 ? e : defect switch
        {
            "revoked" => e with { Revoked = true }, "stale" => e with { CurrentVersion = Guid.NewGuid() },
            "scope" => e with { LearningProfileId = Guid.NewGuid() }, _ => null
        };
        if (update) Assert.Equal(defect == "unavailable" ? LearningSetupWriteOutcome.Unavailable : LearningSetupWriteOutcome.Denied,
            await f.Service.UpdateSelectionsAsync([f.Skill], []));
        else
        {
            var result = await f.Service.ReadAsync(); Assert.Null(result.Snapshot);
            Assert.Equal(defect == "unavailable" ? LearningSetupReadState.Unavailable : LearningSetupReadState.Denied, result.State);
        }
        Assert.Equal(0, f.Writes); Assert.Equal(2, f.AuthCalls);
    }

    [Theory]
    [InlineData(LearningSetupWriteOutcome.Committed)] [InlineData(LearningSetupWriteOutcome.Denied)]
    [InlineData(LearningSetupWriteOutcome.Unavailable)] [InlineData(LearningSetupWriteOutcome.Conflict)]
    [InlineData(LearningSetupWriteOutcome.CommitUnknown)]
    public async Task RepositoryCommitAndRevisionOutcomesPropagateWithoutRetry(LearningSetupWriteOutcome outcome)
    {
        var f = new Fixture { Outcome = outcome }; var before = f.Stored.Snapshot;
        Assert.Equal(outcome, await f.Service.UpdateSelectionsAsync([f.Skill], []));
        Assert.Equal(1, f.Writes); Assert.Same(before, f.Stored.Snapshot);
        Assert.Equal("storage-v1", f.ExpectedRevision); Assert.Equal("catalogue-v1", f.CatalogueRevision);
        // Stale storage/catalogue revision detection belongs to the coordinated repository;
        // Conflict here is supplied by the fake, not proof of actual revision concurrency control.
    }

    [Theory]
    [InlineData("before")] [InlineData("read")] [InlineData("catalogue")]
    public async Task CancellationBeforeWritePropagatesWithoutMutation(string phase)
    {
        using var cts = new CancellationTokenSource(); var f = new Fixture();
        if (phase == "before") cts.Cancel();
        if (phase == "read") f.OnRead = cts.Cancel;
        if (phase == "catalogue") f.OnCatalogue = cts.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.UpdateSelectionsAsync([f.Skill], [], cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken); Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task KnownCommitIsNotMisreportedAsRollbackWhenCancellationArrivesAfterCommit()
    {
        using var cts = new CancellationTokenSource(); var f = new Fixture { OnWrite = cts.Cancel };
        Assert.Equal(LearningSetupWriteOutcome.Committed, await f.Service.UpdateSelectionsAsync([f.Skill], [], cts.Token));
        Assert.True(cts.IsCancellationRequested); Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData(LearningSetupWriteOutcome.Committed)] [InlineData(LearningSetupWriteOutcome.Denied)]
    [InlineData(LearningSetupWriteOutcome.Unavailable)] [InlineData(LearningSetupWriteOutcome.Conflict)]
    [InlineData(LearningSetupWriteOutcome.CommitUnknown)]
    public async Task ConditionalUpdatePreservesExactExpectedRevisionAndNeverRetriesRepositoryOutcome(LearningSetupWriteOutcome outcome)
    {
        var f = new Fixture { Outcome = outcome }; var before = f.Stored.Snapshot!;
        var token = LearningSetupRevision.FromPersisted(Guid.NewGuid(), 7).ToOpaqueToken();
        f.Stored = LearningSetupReadResult.Available(before, token);
        Assert.Equal(outcome, await f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [f.Skill], token));
        Assert.Equal(token, f.ExpectedRevision); Assert.Equal(1, f.Writes); Assert.Equal(2, f.AuthCalls);
        Assert.Equal(before.Selections[0].Id, f.Written!.Selections.Single(s => s.Kind == LearningSkillSelectionKind.Teaching).Id);
        Assert.Same(before.Goals, f.Written.Goals); Assert.Single(before.Selections);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConditionalStaleVersionOrGenerationCannotBeReplacedByLatestRevision(bool differentGeneration)
    {
        var f = new Fixture(); var generation = Guid.NewGuid();
        f.Stored = LearningSetupReadResult.Available(f.Stored.Snapshot!, LearningSetupRevision.FromPersisted(generation, 8).ToOpaqueToken());
        var expected = LearningSetupRevision.FromPersisted(differentGeneration ? Guid.NewGuid() : generation, differentGeneration ? 8 : 7).ToOpaqueToken();
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [], expected));
        Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads); Assert.Single(f.Stored.Snapshot!.Selections);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("storage-v1")]
    public async Task ConditionalMissingInvalidOrLegacyClientPreconditionNeverReadsOrWritesRepository(string? token)
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [], token!));
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads);
    }

    [Fact]
    public async Task ConditionalPathFailsClosedWhenRepositoryStillReturnsLegacyGlobalSnapshotRevision()
    {
        var f = new Fixture(); var token = LearningSetupRevision.FromPersisted(Guid.NewGuid(), 1).ToOpaqueToken();
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [], token));
        Assert.Equal(0, f.Writes); Assert.Equal(0, f.CatalogueReads);
    }

    [Fact]
    public async Task ConditionalPathStillRequiresFreshAuthorityAndPropagatesCancellation()
    {
        var f = new Fixture(); var token = LearningSetupRevision.FromPersisted(Guid.NewGuid(), 1).ToOpaqueToken();
        f.Stored = LearningSetupReadResult.Available(f.Stored.Snapshot!, token);
        f.Change = (call, e) => call == 1 ? e : e with { Revoked = true };
        Assert.Equal(LearningSetupWriteOutcome.Denied, await f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [], token));
        Assert.Equal(0, f.Writes);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.UpdateSelectionsIfCurrentAsync([f.Skill], [], token, cts.Token));
        Assert.Equal(0, f.Writes);
    }
}
