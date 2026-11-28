using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningActivitiesPostgresTests
{
    [LocalPostgresFact]
    public async Task OwnedListingUsesBoundedStablePagesAndDoesNotMixKinds()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var repo = f.Repository();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await repo.CreateAsync(LearningActivityKind.GrowthSelfReport, f.Request(LearningActivityKind.GrowthSelfReport))).Activity!.Id);
        Assert.Equal(LearningActivityOutcome.Saved, (await repo.CreateAsync(LearningActivityKind.MentorshipOffer, f.Request(LearningActivityKind.MentorshipOffer))).Outcome);
        var first = await repo.ListAsync(LearningActivityKind.GrowthSelfReport, limit: 2);
        Assert.Equal(LearningActivityOutcome.Available, first.Outcome); Assert.Equal(2, first.Activities!.Count);
        var second = await f.Repository().ListAsync(LearningActivityKind.GrowthSelfReport, first.Activities[^1].Id, 2);
        Assert.Equal(LearningActivityOutcome.Available, second.Outcome); Assert.Single(second.Activities!);
        Assert.Equal(ids.OrderBy(x => x.ToString("N"), StringComparer.Ordinal), first.Activities.Concat(second.Activities!).Select(x => x.Id));
        Assert.All(first.Activities.Concat(second.Activities!), x => Assert.Equal(LearningActivityKind.GrowthSelfReport, x.Kind));
        Assert.Equal(LearningActivityOutcome.Invalid, (await repo.ListAsync(LearningActivityKind.GrowthSelfReport, limit: 101)).Outcome);
    }

    [LocalPostgresFact]
    public async Task ListingWithholdsTheEntireBatchWhenAuthorityIsRevokedBeforeRelease()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create();
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Repository().CreateAsync(LearningActivityKind.GrowthSelfReport, f.Request(LearningActivityKind.GrowthSelfReport))).Outcome);
        var result = await f.Repository((phase, _) => { if (phase == "read-release") f.Authority.Denied = true; return Task.CompletedTask; })
            .ListAsync(LearningActivityKind.GrowthSelfReport);
        Assert.Equal(LearningActivityOutcome.Denied, result.Outcome); Assert.Null(result.Activities); Assert.Null(result.Activity);
    }

    [LocalPostgresFact]
    public async Task AllSixSupportedRecordKindsPersistAndReopenWithStableIdentityAndContent()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create();
        foreach (var kind in Enum.GetValues<LearningActivityKind>())
        {
            var saved = await f.Repository().CreateAsync(kind, f.Request(kind));
            Assert.Equal(LearningActivityOutcome.Saved, saved.Outcome); Assert.NotNull(saved.Activity);
            var read = await f.Repository().ReadAsync(kind, saved.Activity.Id);
            Assert.Equal(LearningActivityOutcome.Available, read.Outcome); Assert.NotNull(read.Activity);
            Assert.Equal(saved.Activity.Id, read.Activity.Id); Assert.Equal(saved.Activity.Revision, read.Activity.Revision);
            Assert.Equal(saved.Activity.Status, read.Activity.Status); Assert.Equal(saved.Activity.Commitments, read.Activity.Commitments);
            Assert.Equal(saved.Activity.SkillIds, read.Activity.SkillIds);
        }
    }

    [LocalPostgresFact]
    public async Task ExchangePendingPairUniquenessRejectsDifferentTermsAndPreservesExistingRequest()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.SkillExchange;
        var saved = await f.Repository().CreateAsync(kind, f.Request(kind)); Assert.Equal(LearningActivityOutcome.Saved, saved.Outcome);
        var rejected = await f.Repository().CreateAsync(kind, f.Request(kind)); Assert.Equal(LearningActivityOutcome.Conflict, rejected.Outcome);
        var read = await f.Repository().ReadAsync(kind, saved.Activity!.Id);
        Assert.Equal("Pending", read.Activity!.Status); Assert.Equal(saved.Activity.Revision, read.Activity.Revision);
        Assert.Equal(saved.Activity.OfferedSkillId, read.Activity.OfferedSkillId);
        await using var owner = await f.BoundOwner(); var original = await owner.Set<LearningActivityRow>().SingleAsync();
        // Constraint-only verification of the reverse direction; not a claim of a
        // production authenticated peer session or target-eligibility authority.
        var actor = new LearningActivityMember(f.Shared.Actor().UserId, f.Shared.Actor().ModuleProfileId);
        var reverse = LearningActivityState.Create(kind, f.Peer, [f.Peer, actor], new([actor.ProfileId], Guid.NewGuid(), Guid.NewGuid()));
        owner.Add(new LearningActivityRow { Id = reverse.Id, Kind = kind, Generation = Guid.NewGuid(), Version = 1, Status = "Pending",
            PairLow = original.PairLow, PairHigh = original.PairHigh, Members = JsonSerializer.Serialize(reverse.Members), Content = JsonSerializer.Serialize(reverse) });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => owner.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        owner.ChangeTracker.Clear(); Assert.Equal(1, await owner.Set<LearningActivityRow>().CountAsync());
        owner.Add(new LearningActivityRow { Id = reverse.Id, Kind = kind, Generation = Guid.NewGuid(), Version = 1, Status = "Pending",
            PairLow = Guid.NewGuid(), PairHigh = original.PairHigh, Members = JsonSerializer.Serialize(reverse.Members), Content = JsonSerializer.Serialize(reverse) });
        var malformed = await Assert.ThrowsAsync<DbUpdateException>(() => owner.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(malformed.InnerException).SqlState);
        owner.ChangeTracker.Clear(); Assert.Equal(1, await owner.Set<LearningActivityRow>().CountAsync());
    }

    [LocalPostgresFact]
    public async Task SeparateConnectionsCannotCreateTwoPendingExchangesForTheSamePair()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.SkillExchange;
        var results = await Task.WhenAll(f.Repository().CreateAsync(kind, f.Request(kind)), f.Repository().CreateAsync(kind, f.Request(kind)));
        Assert.Equal(1, results.Count(x => x.Outcome == LearningActivityOutcome.Saved));
        Assert.Equal(1, results.Count(x => x.Outcome == LearningActivityOutcome.Conflict));
        await using var owner = await f.BoundOwner(); Assert.Equal(1, await owner.Set<LearningActivityRow>().CountAsync());
    }

    [LocalPostgresFact]
    public async Task ExistingSenderAndProposerCancellationRulesPersistAndTerminalExchangeAllowsFreshIntent()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create();
        foreach (var kind in new[] { LearningActivityKind.SkillExchange, LearningActivityKind.Pact, LearningActivityKind.MentorshipRequest })
        {
            var saved = await f.Repository().CreateAsync(kind, f.Request(kind)); Assert.Equal(LearningActivityOutcome.Saved, saved.Outcome);
            var changed = await f.Repository().ChangeAsync(kind, saved.Activity!.Id, new(LearningActivityAction.Cancel, saved.Activity.Revision));
            Assert.Equal(LearningActivityOutcome.Saved, changed.Outcome); Assert.Equal("Cancelled", changed.Activity!.Status);
            var read = await f.Repository().ReadAsync(kind, saved.Activity.Id); Assert.Equal("Cancelled", read.Activity!.Status);
            Assert.Equal(saved.Activity.Revision.Version + 1, read.Activity.Revision.Version);
        }
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Repository().CreateAsync(LearningActivityKind.SkillExchange, f.Request(LearningActivityKind.SkillExchange))).Outcome);
    }

    [LocalPostgresFact]
    public async Task CircleGoalIdentityOrderAndPrivateTextSurviveAtomicEditsAndReopening()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.Circle;
        var circle = await f.Repository().CreateAsync(kind, f.Request(kind)); Assert.Equal(LearningActivityOutcome.Saved, circle.Outcome);
        var goal = await f.Repository().ChangeAsync(kind, circle.Activity!.Id, new(LearningActivityAction.CreateGoal, circle.Activity.Revision, Text: "private goal"));
        Assert.Equal(LearningActivityOutcome.Saved, goal.Outcome); var identity = Assert.Single(goal.Activity!.Goals).Id;
        var changed = await f.Repository().ChangeAsync(kind, circle.Activity.Id, new(LearningActivityAction.EditGoal, goal.Activity.Revision, identity, "edited private goal"));
        Assert.Equal(LearningActivityOutcome.Saved, changed.Outcome);
        var progress = await f.Repository().ChangeAsync(kind, circle.Activity.Id, new(LearningActivityAction.UpdateGoalProgress, changed.Activity!.Revision, identity, ProgressPercent: 60));
        Assert.Equal(LearningActivityOutcome.Saved, progress.Outcome);
        var read = await f.Repository().ReadAsync(kind, circle.Activity.Id); var stored = Assert.Single(read.Activity!.Goals);
        Assert.Equal(identity, stored.Id); Assert.Equal("edited private goal", stored.Text); Assert.Equal(60, stored.ProgressPercent);
        var invalid = await f.Repository().ChangeAsync(kind, circle.Activity.Id, new(LearningActivityAction.UpdateGoalProgress, read.Activity.Revision, identity, ProgressPercent: 101));
        Assert.Equal(LearningActivityOutcome.Invalid, invalid.Outcome);
        Assert.Equal(read.Activity.Revision, (await f.Repository().ReadAsync(kind, circle.Activity.Id)).Activity!.Revision);
    }

    [LocalPostgresFact]
    public async Task StaleRevisionAndUnauthorizedParticipantTransitionPreserveCommittedState()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.SkillExchange;
        var saved = await f.Repository().CreateAsync(kind, f.Request(kind)); var item = saved.Activity!;
        var denied = await f.Repository().ChangeAsync(kind, item.Id, new(LearningActivityAction.Accept, item.Revision));
        Assert.Equal(LearningActivityOutcome.Denied, denied.Outcome);
        var stale = await f.Repository().ChangeAsync(kind, item.Id, new(LearningActivityAction.Cancel, item.Revision with { Version = 99 }));
        Assert.Equal(LearningActivityOutcome.Conflict, stale.Outcome);
        var read = await f.Repository().ReadAsync(kind, item.Id); Assert.Equal("Pending", read.Activity!.Status); Assert.Equal(item.Revision, read.Activity.Revision);
        await using var owner = await f.BoundOwner(); var row = await owner.Set<LearningActivityRow>().SingleAsync();
        var legacy = JsonNode.Parse(row.Content)!; legacy["unapprovedLegacyField"] = "private data must not disappear";
        row.Content = legacy.ToJsonString(); await owner.SaveChangesAsync(); await owner.Entry(row).ReloadAsync(); var stored = row.Content;
        Assert.Equal(LearningActivityOutcome.Unavailable, (await f.Repository().ChangeAsync(kind, item.Id, new(LearningActivityAction.Cancel, item.Revision))).Outcome);
        await owner.Entry(row).ReloadAsync(); Assert.Equal(stored, row.Content); Assert.Equal(item.Revision.Version, row.Version);
    }

    [LocalPostgresFact]
    public async Task UnavailableAndDeniedAuthorityNeverMutateOrDisclosePrivateData()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.GrowthSelfReport;
        f.Authority.Unavailable = true; Assert.Equal(LearningActivityOutcome.Unavailable, (await f.Repository().CreateAsync(kind, f.Request(kind))).Outcome);
        f.Authority.Unavailable = false; f.Authority.Denied = true;
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Repository().CreateAsync(kind, f.Request(kind))).Outcome);
        await using var owner = await f.BoundOwner(); Assert.Empty(await owner.Set<LearningActivityRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RevocationBeforeCommitRollsBackAndReadReleaseRevocationWithholdsContent()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.GrowthSelfReport;
        var denied = await f.Repository((phase, _) => { if (phase == "before-commit") f.Authority.Denied = true; return Task.CompletedTask; }).CreateAsync(kind, f.Request(kind));
        Assert.Equal(LearningActivityOutcome.Denied, denied.Outcome);
        await using (var owner = await f.BoundOwner()) Assert.Empty(await owner.Set<LearningActivityRow>().ToListAsync());
        f.Authority.Denied = false; var saved = await f.Repository().CreateAsync(kind, f.Request(kind));
        var read = await f.Repository((phase, _) => { if (phase == "read-release") f.Authority.Denied = true; return Task.CompletedTask; }).ReadAsync(kind, saved.Activity!.Id);
        Assert.Equal(LearningActivityOutcome.Denied, read.Outcome); Assert.Null(read.Activity);
    }

    [LocalPostgresFact]
    public async Task CancellationBeforeCommitRollsBackEveryInsertedRecord()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); using var cancel = new CancellationTokenSource();
        var repo = f.Repository((phase, _) => { if (phase == "before-commit") cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.CreateAsync(LearningActivityKind.Pact, f.Request(LearningActivityKind.Pact), cancel.Token));
        await using var owner = await f.BoundOwner(); Assert.Empty(await owner.Set<LearningActivityRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RestrictedRuntimeForceRlsDeniesForeignAccountAndHasNoMembershipRewriteDeleteOrBypass()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create();
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Repository().CreateAsync(LearningActivityKind.GrowthSelfReport, f.Request(LearningActivityKind.GrowthSelfReport))).Outcome);
        await using var runtime = f.Shared.OpenRuntime(); await using var db = new IsolatedLearningActivitiesDbContext(new DbContextOptionsBuilder<IsolatedLearningActivitiesDbContext>().UseNpgsql(runtime.Database.GetConnectionString()!).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Shared.Owner.Subject.ToString()},false),set_config('andril.account',{Guid.NewGuid().ToString()},false),set_config('andril.module','learning',false)");
        Assert.Empty(await db.Set<LearningActivityRow>().ToListAsync());
        Assert.Equal(0, await db.Set<LearningActivityRow>().ExecuteUpdateAsync(x => x.SetProperty(r => r.Version, r => r.Version + 1)));
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolbypassrls OR rolsuper OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning_activities_isolated.activities','DELETE') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_column_privilege(current_user,'andril_learning_activities_isolated.activities','members','UPDATE') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_learning_activities_isolated.activities'::regclass").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task ApprovedMentorshipPendingPairConflictsAndGrowthStillCannotMutate()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); var kind = LearningActivityKind.MentorshipRequest;
        var request = f.Request(kind); Assert.Equal(LearningActivityOutcome.Saved, (await f.Repository().CreateAsync(kind, request)).Outcome);
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Repository().CreateAsync(kind, request)).Outcome);
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Repository().CreateAsync(kind, request with { RelatedOfferId = Guid.NewGuid() })).Outcome);
        var growth = await f.Repository().CreateAsync(LearningActivityKind.GrowthSelfReport, f.Request(LearningActivityKind.GrowthSelfReport));
        var mutation = await f.Repository().ChangeAsync(LearningActivityKind.GrowthSelfReport, growth.Activity!.Id, new(LearningActivityAction.Cancel, growth.Activity.Revision));
        Assert.Equal(LearningActivityOutcome.Unavailable, mutation.Outcome);
        Assert.Equal(growth.Activity.Revision, (await f.Repository().ReadAsync(LearningActivityKind.GrowthSelfReport, growth.Activity.Id)).Activity!.Revision);
    }

    [LocalPostgresFact]
    public async Task IndependentMentorshipRequestsConflictUntilThePriorPairBecomesTerminal()
    {
        await using var f=await LearningActivitiesPostgresFixture.Create();var kind=LearningActivityKind.MentorshipRequest;
        var results=await Task.WhenAll(f.Repository().CreateAsync(kind,f.Request(kind)),f.Repository().CreateAsync(kind,f.Request(kind)));
        var saved=Assert.Single(results,x=>x.Outcome==LearningActivityOutcome.Saved).Activity!;
        Assert.Single(results,x=>x.Outcome==LearningActivityOutcome.Conflict);
        await using(var owner=await f.BoundOwner())
        {
            var original=await owner.Set<LearningActivityRow>().SingleAsync();
            var actor=new LearningActivityMember(f.Shared.Actor().UserId,f.Shared.Actor().ModuleProfileId);
            var reverse=LearningActivityState.Create(kind,f.Peer,[f.Peer,actor],new([actor.ProfileId],RelatedOfferId:Guid.NewGuid()));
            owner.Add(new LearningActivityRow{Id=reverse.Id,Kind=kind,Generation=Guid.NewGuid(),Version=1,Status="Pending",PairLow=original.PairLow,PairHigh=original.PairHigh,Members=JsonSerializer.Serialize(reverse.Members),Content=JsonSerializer.Serialize(reverse)});
            var error=await Assert.ThrowsAsync<DbUpdateException>(()=>owner.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation,Assert.IsType<PostgresException>(error.InnerException).SqlState);
            owner.ChangeTracker.Clear();Assert.Equal(1,await owner.Set<LearningActivityRow>().CountAsync());
        }
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Repository().ChangeAsync(kind,saved.Id,new(LearningActivityAction.Cancel,saved.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Repository().CreateAsync(kind,f.Request(kind))).Outcome);
    }
}
