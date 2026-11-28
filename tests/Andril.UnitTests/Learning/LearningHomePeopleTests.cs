using Andril.Learning.Discovery;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningHomePeopleTests
{
    private sealed class Fixture : ICurrentUser, ILearningSetupAuthorityReader, ILearningSetupRepository, ILearningSkillCatalogueReader, ILearningPeopleDiscoveryReader
    {
        public Guid? UserId { get; } = Guid.NewGuid(); public bool IsAuthenticated => true;
        internal Guid Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Version = Guid.NewGuid();
        internal bool Revoked, Unavailable, Complete = true;
        internal int Reads, AuthorityReads;
        internal Action? OnRead, OnPeople;
        internal LearningPeopleEvidence[] Candidates = [];
        internal LearningSetupSnapshot Snapshot;
        internal Fixture()
        {
            var p = new LearningPreferences(Account, Profile); var goal = p.CreateGoal("private home goal");
            Snapshot = new(new(Account, Profile), [], [LearningPersonalGoal.CreateFromPreferences(p, goal.Id)]);
        }
        internal LearningSetupAccessBoundary Access => new(this, this);
        internal LearningHomeApplicationService Home => new(new(Access, this, this), Access);
        internal LearningPeopleDiscoveryService People => new(Access, this);
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); AuthorityReads++;
            return Task.FromResult<LearningSetupAccessEvidence?>(Unavailable ? null : new(subject, Account, Profile, Account, "learning", operation,
                true, true, true, true, true, Revoked, Complete, Version, Version));
        }
        public Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
        { Reads++; OnRead?.Invoke(); return Task.FromResult(LearningSetupReadResult.Available(Snapshot, "durable-revision")); }
        public Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Home must never mutate.");
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(LearningSkillCatalogueReadResult.Unavailable());
        public async Task<T?> WithAuthorizedSearchAsync<T>(LearningSetupScope scope, string query, int limit, Func<LearningPeopleSnapshot,CancellationToken,Task<T>> project, CancellationToken cancellationToken = default) where T:class
        { OnPeople?.Invoke(); return await project(new(Account, Profile, "current", Candidates), cancellationToken); }
        internal LearningPeopleEvidence Candidate() => new(Guid.NewGuid(), "learning", "visible person", LearningDiscoveryLifecycle.Active,
            LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed);
    }
    [Fact]
    public async Task HomeProjectsExistingOwnedGoalsAndRevisionWithoutInventedMetrics()
    {
        var f = new Fixture(); var result = await f.Home.ReadAsync(); Assert.Equal(LearningSetupReadState.Available, result.State);
        Assert.Equal("durable-revision", result.Home!.Revision); Assert.Equal(f.Snapshot.Goals[0].Id, Assert.Single(result.Home.Goals).Id);
        Assert.Empty(result.Home.Skills); Assert.Equal(1, f.Reads); Assert.True(f.AuthorityReads >= 4);
        Assert.DoesNotContain("private home goal", result.Home.ToString());
    }
    [Theory]
    [InlineData("incomplete")] [InlineData("revoked")] [InlineData("unavailable")]
    public async Task HomeRequiresExistingHomeEligibilityBeforeReading(string defect)
    {
        var f = new Fixture { Complete = defect != "incomplete", Revoked = defect == "revoked", Unavailable = defect == "unavailable" };
        var result = await f.Home.ReadAsync(); Assert.NotEqual(LearningSetupReadState.Available, result.State); Assert.Null(result.Home); Assert.Equal(0, f.Reads);
    }
    [Fact]
    public async Task HomeRevocationDuringReadWithholdsAllContent()
    { var f = new Fixture(); f.OnRead = () => f.Revoked = true; var r = await f.Home.ReadAsync(); Assert.Equal(LearningSetupReadState.Denied, r.State); Assert.Null(r.Home); }
    [Fact]
    public async Task OrdinaryPeopleUsesIndependentPurposeAndRejectsForeignHiddenBlockedAndContradictoryEvidence()
    {
        var f = new Fixture(); var allowed = f.Candidate(); var contradictory = f.Candidate();
        f.Candidates = [allowed, allowed, f.Candidate() with { ProfileId = f.Profile }, f.Candidate() with { Module = "dating" },
            f.Candidate() with { Blocking = LearningDiscoveryDecision.Denied }, f.Candidate() with { Visibility = LearningDiscoveryDecision.Unknown },
            f.Candidate() with { OrdinaryDisplayNameDisclosure = LearningDiscoveryDecision.Unknown }, f.Candidate() with { Lifecycle = LearningDiscoveryLifecycle.Inactive },
            contradictory, contradictory with { DisplayName = "different" }];
        var result = await f.People.SearchAsync("person"); Assert.Equal(LearningPeopleOutcome.Available, result.Outcome);
        Assert.Equal(allowed.ProfileId, Assert.Single(result.People!).ProfileId);
    }
    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("a\nb")]
    public async Task InvalidDiscoveryQueriesNeverReachAuthority(string query)
    { var f = new Fixture(); Assert.Equal(LearningPeopleOutcome.Invalid,(await f.People.SearchAsync(query)).Outcome); Assert.Equal(0, f.AuthorityReads); }
    [Fact]
    public async Task PeopleRevocationAndCancellationCannotReleasePrivateCards()
    {
        var f = new Fixture(); f.Candidates = [f.Candidate()]; f.OnPeople = () => f.Revoked = true;
        Assert.NotEqual(LearningPeopleOutcome.Available, (await f.People.SearchAsync("person")).Outcome);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.People.SearchAsync("person", cancellationToken: cancel.Token));
    }
}
