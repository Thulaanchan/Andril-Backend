using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Learning;

/// <summary>
/// Isolated executable repository contract only. The synthetic authority, catalogue and storage
/// share one test fence. This is NOT an EF adapter, production coordination mechanism, durable
/// commit, authorization receipt or proof of PostgreSQL atomicity. No runtime registration.
/// </summary>
public sealed class IsolatedLearningSetupRepositoryTests
{
    private sealed class Store : ILearningSetupRepository, ILearningSetupAuthorityReader, ICurrentUser
    {
        internal readonly SemaphoreSlim Fence = new(1, 1);
        internal readonly Guid Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Skill = Guid.NewGuid();
        private readonly Guid version = Guid.NewGuid();
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        internal LearningSetupSnapshot Snapshot;
        internal string Revision = "initial", CatalogueRevision = "catalogue";
        internal LearningSkillCatalogue Catalogue;
        internal bool Unavailable, Revoked, StorageUnavailable;
        internal Func<LearningSetupAccessEvidence, LearningSetupAccessEvidence>? Change;
        internal Action? BeforeRelease;
        internal int Mutations, Checks;
        internal LearningSetupScope Scope => new(Account, Profile);
        private LearningSetupAccessBoundary Access => new(this, this);

        internal Store()
        {
            var p = new LearningPreferences(Account, Profile);
            p.UpdateSelections([], [Skill], []);
            var goal = p.CreateGoal("SYNTHETIC-PRIVATE-GOAL");
            Snapshot = new(Scope,
                [LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, Skill, LearningSkillSelectionKind.Teaching)],
                [LearningPersonalGoal.CreateFromPreferences(p, goal.Id)]);
            Catalogue = new([new(Skill, true, LearningSkillCatalogueStatus.Active)]);
        }

        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Checks++;
            LearningSetupAccessEvidence e = new(subject, Account, Profile, Account, "learning", operation,
                true, true, true, true, true, Revoked, false, version, version);
            return Task.FromResult(Unavailable ? null : Change?.Invoke(e) ?? e);
        }

        private async Task<LearningSetupAccessState> Authorize(LearningSetupScope scope,
            LearningSetupOperation operation, CancellationToken ct)
        {
            var result = await Access.CheckAsync(operation, ct);
            if (result.State != LearningSetupAccessState.Allowed) return result.State;
            return result.Scope!.AccountId == scope.AccountId && result.Scope.LearningProfileId == scope.LearningProfileId
                && result.Scope.Module == scope.Module ? LearningSetupAccessState.Allowed : LearningSetupAccessState.Denied;
        }

        public async Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scope);
            await Fence.WaitAsync(cancellationToken);
            try
            {
                var state = await Authorize(scope, LearningSetupOperation.ReadSetup, cancellationToken);
                if (state == LearningSetupAccessState.Denied) return LearningSetupReadResult.Denied();
                if (state != LearningSetupAccessState.Allowed || StorageUnavailable) return LearningSetupReadResult.Unavailable();
                var snapshot = Snapshot; var revision = Revision;
                BeforeRelease?.Invoke();
                state = await Authorize(scope, LearningSetupOperation.ReadSetup, cancellationToken);
                return state == LearningSetupAccessState.Denied ? LearningSetupReadResult.Denied()
                    : state != LearningSetupAccessState.Allowed ? LearningSetupReadResult.Unavailable()
                    : LearningSetupReadResult.Available(snapshot, revision);
            }
            finally { Fence.Release(); }
        }

        public async Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement,
            string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            await Fence.WaitAsync(cancellationToken);
            try
            {
                var state = await Authorize(replacement.Scope, LearningSetupOperation.UpdateSetup, cancellationToken);
                if (state == LearningSetupAccessState.Denied) return LearningSetupWriteOutcome.Denied;
                if (state != LearningSetupAccessState.Allowed || StorageUnavailable) return LearningSetupWriteOutcome.Unavailable;
                if (string.IsNullOrWhiteSpace(expectedStorageRevision) || string.IsNullOrWhiteSpace(catalogueRevision)
                    || expectedStorageRevision != Revision || catalogueRevision != CatalogueRevision)
                    return LearningSetupWriteOutcome.Conflict;
                // This slice cannot edit/purge goals or change existing selection identities.
                if (!Snapshot.Goals.Select(g => (g.Id, g.Text)).OrderBy(g => g.Id)
                    .SequenceEqual(replacement.Goals.Select(g => (g.Id, g.Text)).OrderBy(g => g.Id)))
                    return LearningSetupWriteOutcome.Conflict;
                var previous = Snapshot.Selections.ToDictionary(s => (s.Kind, s.SkillId), s => s.Id);
                if (replacement.Selections.Any(s => previous.TryGetValue((s.Kind, s.SkillId), out var id) && id != s.Id)
                    || replacement.Selections.Any(s => Snapshot.Selections.Any(old => old.Id == s.Id && (old.Kind != s.Kind || old.SkillId != s.SkillId))))
                    return LearningSetupWriteOutcome.Conflict;
                Catalogue.ValidateNewSelections(replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId),
                    replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Learning).Select(s => s.SkillId));
                var nextRevision = Guid.NewGuid().ToString("N");
                BeforeRelease?.Invoke();
                state = await Authorize(replacement.Scope, LearningSetupOperation.UpdateSetup, cancellationToken);
                if (state == LearningSetupAccessState.Denied) return LearningSetupWriteOutcome.Denied;
                if (state != LearningSetupAccessState.Allowed) return LearningSetupWriteOutcome.Unavailable;
                cancellationToken.ThrowIfCancellationRequested();
                // Single in-memory publication while fenced, not a durable database commit.
                Snapshot = replacement; Revision = nextRevision; Mutations++;
                return LearningSetupWriteOutcome.Committed;
            }
            finally { Fence.Release(); }
        }

        internal LearningSetupSnapshot Replacement(bool preserveIdentity = true, bool preserveGoals = true)
        {
            var p = new LearningPreferences(Account, Profile); p.UpdateSelections([], [Skill], [Skill]);
            var rows = new[] { LearningSkillSelection.CreateFromPreferences(preserveIdentity ? Snapshot.Selections[0].Id : Guid.NewGuid(), p, Skill, LearningSkillSelectionKind.Teaching),
                LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, Skill, LearningSkillSelectionKind.Learning) };
            // Public composition preserves the original goal identity via the existing immutable snapshot helper.
            if (!preserveGoals) return new(Scope, rows, []);
            var method = typeof(LearningSetupSnapshot).GetMethod("WithSelections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (LearningSetupSnapshot)method.Invoke(Snapshot, [rows])!;
        }
    }

    [Fact]
    public async Task OwnerReadsAndAtomicReplacementPreserveGoalsIdentitiesAndEarlierSnapshots()
    {
        var s = new Store(); var before = await s.ReadAsync(s.Scope); var next = s.Replacement();
        Assert.Equal(LearningSetupReadState.Available, before.State);
        Assert.Equal(LearningSetupWriteOutcome.Committed, await s.ReplaceAsync(next, before.StorageRevision!, s.CatalogueRevision));
        var after = await s.ReadAsync(s.Scope);
        Assert.Same(next, after.Snapshot); Assert.NotEqual(before.StorageRevision, after.StorageRevision);
        Assert.Single(before.Snapshot!.Selections); Assert.Equal(2, after.Snapshot!.Selections.Count);
        Assert.Equal(before.Snapshot.Selections[0].Id, after.Snapshot.Selections[0].Id);
        Assert.Same(before.Snapshot.Goals, after.Snapshot.Goals); Assert.Equal(1, s.Mutations);
    }

    [Theory]
    [InlineData("foreign-account")] [InlineData("foreign-profile")] [InlineData("module")]
    [InlineData("inactive")] [InlineData("stale")] [InlineData("revoked")] [InlineData("permission")]
    public async Task InvalidAuthorityOrScopeNeverReleasesDataOrMutates(string defect)
    {
        var s = new Store(); var before = s.Snapshot; var replacement = s.Replacement();
        s.Change = e => defect switch {
            "module" => e with { Module = "social" }, "inactive" => e with { MembershipActive = false },
            "stale" => e with { CurrentVersion = Guid.NewGuid() }, "revoked" => e with { Revoked = true },
            "permission" => e with { OperationAllowed = false }, _ => e };
        var scope = new LearningSetupScope(defect == "foreign-account" ? Guid.NewGuid() : s.Account,
            defect == "foreign-profile" ? Guid.NewGuid() : s.Profile);
        if (defect.StartsWith("foreign", StringComparison.Ordinal)) replacement = new(scope, [], []);
        var read = await s.ReadAsync(scope);
        Assert.Equal(LearningSetupReadState.Denied, read.State); Assert.Null(read.Snapshot);
        Assert.Equal(LearningSetupWriteOutcome.Denied, await s.ReplaceAsync(replacement, s.Revision, s.CatalogueRevision));
        Assert.Same(before, s.Snapshot); Assert.Equal(0, s.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnavailableDependenciesFailClosed(bool storage)
    {
        var s = new Store { StorageUnavailable = storage, Unavailable = !storage }; var before = s.Snapshot;
        Assert.Equal(LearningSetupReadState.Unavailable, (await s.ReadAsync(s.Scope)).State);
        Assert.Equal(LearningSetupWriteOutcome.Unavailable, await s.ReplaceAsync(s.Replacement(), s.Revision, s.CatalogueRevision));
        Assert.Same(before, s.Snapshot); Assert.Equal(0, s.Mutations);
    }

    [Theory]
    [InlineData("storage")] [InlineData("catalogue")] [InlineData("blank")]
    [InlineData("identity")] [InlineData("goals")]
    public async Task ConflictsPreserveAllState(string defect)
    {
        var s = new Store(); var before = s.Snapshot; var revision = s.Revision;
        Assert.Equal(LearningSetupWriteOutcome.Conflict, await s.ReplaceAsync(s.Replacement(defect != "identity", defect != "goals"),
            defect == "storage" ? "stale" : defect == "blank" ? " " : revision,
            defect == "catalogue" ? "stale" : s.CatalogueRevision));
        Assert.Same(before, s.Snapshot); Assert.Equal(revision, s.Revision); Assert.Equal(0, s.Mutations);
    }

    [Fact]
    public async Task InvalidCatalogueCannotPublishPartialReplacement()
    {
        var s = new Store(); var before = s.Snapshot; s.Catalogue = new([]);
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReplaceAsync(s.Replacement(), s.Revision, s.CatalogueRevision));
        Assert.Same(before, s.Snapshot); Assert.Equal(0, s.Mutations);
    }

    [Fact]
    public async Task RevocationAtReleaseAndCommitIsNotCached()
    {
        var s = new Store(); var before = s.Snapshot; s.BeforeRelease = () => s.Revoked = true;
        Assert.Equal(LearningSetupReadState.Denied, (await s.ReadAsync(s.Scope)).State);
        s.Revoked = false;
        Assert.Equal(LearningSetupWriteOutcome.Denied, await s.ReplaceAsync(s.Replacement(), s.Revision, s.CatalogueRevision));
        Assert.Same(before, s.Snapshot); Assert.Equal(4, s.Checks); Assert.Equal(0, s.Mutations);
    }

    [Fact]
    public async Task CompetingReplacementsWithSameRevisionHaveExactlyOneWinner()
    {
        var s = new Store(); var revision = s.Revision; var first = s.Replacement(); var second = s.Replacement();
        var outcomes = await Task.WhenAll(Task.Run(() => s.ReplaceAsync(first, revision, s.CatalogueRevision)),
            Task.Run(() => s.ReplaceAsync(second, revision, s.CatalogueRevision)));
        Assert.Single(outcomes, o => o == LearningSetupWriteOutcome.Committed);
        Assert.Single(outcomes, o => o == LearningSetupWriteOutcome.Conflict); Assert.Equal(1, s.Mutations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeReleaseOrPublicationPreservesState(bool write)
    {
        var s = new Store(); var before = s.Snapshot; using var cts = new CancellationTokenSource();
        s.BeforeRelease = cts.Cancel;
        if (write) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ReplaceAsync(s.Replacement(), s.Revision, s.CatalogueRevision, cts.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ReadAsync(s.Scope, cts.Token));
        Assert.Same(before, s.Snapshot); Assert.Equal(0, s.Mutations);
    }
}
