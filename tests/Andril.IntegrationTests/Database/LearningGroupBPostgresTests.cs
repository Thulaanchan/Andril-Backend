using System.Text.Json;
using Andril.Learning.Activities;
using Andril.Learning.Discovery;
using Andril.Learning.Services;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningGroupBPostgresTests
{
    private const LearningActivityKind Kind = LearningActivityKind.Connection;
    [LocalPostgresFact]
    public async Task EligibleThirdPartyAndCrossModuleIdentityCannotAccessTheRelationship()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); var item = (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Activity!;
        var foreign = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Activities.Shared.Shared.Shared.Shared.Database, new(foreign.Account, foreign.Profiles["learning"]), foreign.Subject);
        var repo = f.Activities.RepositoryFor(foreign, f.Pairs);
        Assert.Equal(LearningActivityOutcome.Denied, (await repo.ReadAsync(Kind, item.Id)).Outcome);
        Assert.Equal(LearningActivityOutcome.Denied, (await repo.ChangeAsync(Kind, item.Id, new(LearningActivityAction.Accept, item.Revision))).Outcome);
        var original = f.Peer.Profiles["learning"]; f.Peer.Profiles["learning"] = f.Peer.Profiles["hobbies"];
        try { Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections(true).ReadAsync(Kind, item.Id)).Outcome); }
        finally { f.Peer.Profiles["learning"] = original; }
        Assert.Equal("Pending", (await f.Connections().ReadAsync(Kind, item.Id)).Activity!.Status);
    }
    [LocalPostgresFact]
    public async Task HomeLoadsActualPostgresPreferencesAndGoalsAndRequiresCompletionWithoutMutatingThem()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var actor = f.Activities.Shared.Actor(); var skill = Guid.NewGuid();
        var preferences = new LearningPreferences(actor.UserId, actor.ModuleProfileId); preferences.UpdateSelections([], [skill], []);
        var goal = preferences.CreateGoal("private home goal from PostgreSQL");
        var selection = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, skill, LearningSkillSelectionKind.Teaching);
        await using (var source = await f.Activities.Shared.BoundOwner())
        {
            await using var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(source.Database.GetConnectionString()!).Options);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Activities.Shared.Owner.Subject.ToString()},false),set_config('andril.account',{actor.UserId.ToString()},false),set_config('andril.module','learning',false)");
            db.Add(selection); db.Add(LearningPersonalGoal.CreateFromPreferences(preferences, goal.Id)); await db.SaveChangesAsync();
        }
        var read = await f.Home.ReadAsync(); Assert.Equal(LearningSetupReadState.Available, read.State);
        Assert.Equal(goal.Id, Assert.Single(read.Home!.Goals).Id); Assert.Equal(selection.Id, Assert.Single(read.Home.Skills).Id);
        Assert.Equal(skill, read.Home.Skills[0].SkillId); Assert.False(string.IsNullOrWhiteSpace(read.Home.Revision));
        Assert.Equal(read.Home.Revision, (await f.Home.ReadAsync()).Home!.Revision);
        f.Owner.Complete = false; Assert.Equal(LearningSetupReadState.Denied, (await f.Home.ReadAsync()).State);
        f.Owner.Complete = true; f.Owner.Revoked = true; Assert.Equal(LearningSetupReadState.Denied, (await f.Home.ReadAsync()).State);
    }
    [LocalPostgresFact]
    public async Task RealOwnerAndRecipientSessionsPersistAcceptanceAndEitherPartyDisconnection()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var pending = await f.Connections().CreateAsync(Kind, f.ConnectRequest()); Assert.Equal(LearningActivityOutcome.Saved, pending.Outcome);
        var item = pending.Activity!; Assert.Equal("Pending", item.Status);
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections().ChangeAsync(Kind, item.Id, new(LearningActivityAction.Accept, item.Revision))).Outcome);
        var accepted = await f.Connections(true).ChangeAsync(Kind, item.Id, new(LearningActivityAction.Accept, item.Revision));
        Assert.Equal(LearningActivityOutcome.Saved, accepted.Outcome); Assert.Equal("Accepted", accepted.Activity!.Status);
        Assert.Equal("Accepted", (await f.Connections().ReadAsync(Kind, item.Id)).Activity!.Status);
        var disconnected = await f.Connections().ChangeAsync(Kind, item.Id, new(LearningActivityAction.Disconnect, accepted.Activity.Revision));
        Assert.Equal(LearningActivityOutcome.Saved, disconnected.Outcome); Assert.Equal("Disconnected", disconnected.Activity!.Status);
        var next = await f.Connections(true).CreateAsync(Kind, f.ConnectRequest(true)); Assert.Equal(LearningActivityOutcome.Saved, next.Outcome);
        var nextAccepted = await f.Connections().ChangeAsync(Kind, next.Activity!.Id, new(LearningActivityAction.Accept, next.Activity.Revision));
        Assert.Equal(LearningActivityOutcome.Saved, nextAccepted.Outcome);
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Connections(true).ChangeAsync(Kind, next.Activity.Id, new(LearningActivityAction.Disconnect, nextAccepted.Activity!.Revision))).Outcome);
    }
    [LocalPostgresFact]
    public async Task ReversedPendingAndActiveRequestsConflictButTerminalHistoryRemains()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var first = await f.Connections().CreateAsync(Kind, f.ConnectRequest()); var item = first.Activity!;
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Connections(true).CreateAsync(Kind, f.ConnectRequest(true))).Outcome);
        var active = await f.Connections(true).ChangeAsync(Kind, item.Id, new(LearningActivityAction.Accept, item.Revision));
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Outcome);
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Connections(true).CreateAsync(Kind, f.ConnectRequest(true))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Connections(true).ChangeAsync(Kind, item.Id, new(LearningActivityAction.Disconnect, active.Activity!.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved, (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Outcome);
        Assert.Equal("Disconnected", (await f.Connections().ReadAsync(Kind, item.Id)).Activity!.Status);
    }
    [LocalPostgresFact]
    public async Task IndependentReversedRequestsCannotCommitTwoLiveRelationships()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var result = await Task.WhenAll(f.Connections().CreateAsync(Kind, f.ConnectRequest()), f.Connections(true).CreateAsync(Kind, f.ConnectRequest(true)));
        Assert.Equal(1, result.Count(x => x.Outcome == LearningActivityOutcome.Saved)); Assert.Equal(1, result.Count(x => x.Outcome == LearningActivityOutcome.Conflict));
        await using var owner = await f.Activities.BoundOwner(); Assert.Equal(1, await owner.Set<LearningActivityRow>().CountAsync(x => x.Kind == Kind));
    }
    [LocalPostgresFact]
    public async Task RecipientDeclineAndSenderCancelAreDurableAndDoNotImplyCommunicationRights()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var first = (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Activity!;
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections(true).ChangeAsync(Kind, first.Id, new(LearningActivityAction.Cancel, first.Revision))).Outcome);
        Assert.Equal("Declined", (await f.Connections(true).ChangeAsync(Kind, first.Id, new(LearningActivityAction.Decline, first.Revision))).Activity!.Status);
        var second = (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Activity!;
        Assert.Equal("Cancelled", (await f.Connections().ChangeAsync(Kind, second.Id, new(LearningActivityAction.Cancel, second.Revision))).Activity!.Status);
        Assert.Empty((await f.Connections().ReadAsync(Kind, second.Id)).Activity!.Goals);
    }
    [LocalPostgresFact]
    public async Task BlockingAndInactiveLifecycleDenyReadListAndMutationWithoutDeletingHistory()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); var item = (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Activity!;
        f.Pairs.Blocked = true;
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections().ReadAsync(Kind, item.Id)).Outcome);
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections(true).ListAsync(Kind)).Outcome);
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections().ChangeAsync(Kind, item.Id, new(LearningActivityAction.Cancel, item.Revision))).Outcome);
        f.Pairs.Blocked = false; f.Activities.Shared.Owner.Denied = true;
        Assert.Equal(LearningActivityOutcome.Denied, (await f.Connections().ReadAsync(Kind, item.Id)).Outcome);
        f.Activities.Shared.Owner.Denied = false;
        Assert.Equal("Pending", (await f.Connections().ReadAsync(Kind, item.Id)).Activity!.Status);
    }
    [LocalPostgresFact]
    public async Task ConnectionRevocationCancellationAndStaleRevisionPreserveCommittedRows()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        var denied = await f.Connections(checkpoint: (phase, _) => { if (phase == "before-commit") f.Pairs.Blocked = true; return Task.CompletedTask; }).CreateAsync(Kind, f.ConnectRequest());
        Assert.Equal(LearningActivityOutcome.Denied, denied.Outcome); f.Pairs.Blocked = false;
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Connections(checkpoint: (phase, _) => { if (phase == "before-commit") cancel.Cancel(); return Task.CompletedTask; }).CreateAsync(Kind, f.ConnectRequest(), cancel.Token));
        var item = (await f.Connections().CreateAsync(Kind, f.ConnectRequest())).Activity!;
        Assert.Equal(LearningActivityOutcome.Conflict, (await f.Connections(true).ChangeAsync(Kind, item.Id, new(LearningActivityAction.Accept, item.Revision with { Version = 9 }))).Outcome);
        Assert.Equal("Pending", (await f.Connections().ReadAsync(Kind, item.Id)).Activity!.Status);
        await using var owner = await f.Activities.BoundOwner(); Assert.Single(await owner.Set<LearningActivityRow>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task OrdinaryPeopleSearchFiltersBeforeMatchingAndReturnsOnlyAuthorizedNameAndProfile()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); var visible = f.Evidence();
        var hidden = f.Evidence(name: "private needle"); hidden.Visible = false;
        var unclassified = f.Evidence(name: "unclassified needle"); unclassified.OrdinaryNameDisclosure = false;
        var blocked = f.Evidence(); blocked.BlocksClear = false;
        var inactive = f.Evidence(); inactive.Active = false;
        await using (var db = await f.BoundOwner()) { db.AddRange(visible, hidden, unclassified, blocked, inactive); await db.SaveChangesAsync(); }
        var read = await f.People().SearchAsync("visible"); Assert.Equal(LearningPeopleOutcome.Available, read.Outcome);
        Assert.Equal(visible.TargetId, Assert.Single(read.People!).ProfileId);
        Assert.Empty((await f.People().SearchAsync("needle")).People!);
        var json = JsonSerializer.Serialize(read); Assert.DoesNotContain(f.Activities.Shared.Actor().UserId.ToString(), json); Assert.DoesNotContain("private needle", json);
    }
    [LocalPostgresFact]
    public async Task ClassroomOrdinaryDiscoveryUsesActualM5VisibilityLifecycleBlockAndMinimalProjection()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); var allowed = f.Evidence("Classroom", "classroom visible");
        var hidden = f.Evidence("Classroom", "classroom private"); hidden.Public = false;
        var blocked = f.Evidence("Classroom", "classroom blocked"); blocked.BlocksClear = false;
        var closed = f.Evidence("Classroom", "classroom inactive"); closed.Active = false;
        var restricted = f.Evidence("Classroom", "classroom restricted"); restricted.MembershipAllowed = false;
        await using (var db = await f.BoundOwner()) { db.AddRange(allowed, hidden, blocked, closed, restricted); await db.SaveChangesAsync(); }
        var result = await f.Classrooms().SearchAsync("classroom"); Assert.Equal("Available", result.Outcome);
        var card = Assert.Single(result.Classrooms!); Assert.Equal(allowed.TargetId, card.Id); Assert.Equal("learning", card.Module);
        var serialized = JsonSerializer.Serialize(card); Assert.DoesNotContain("Member", serialized); Assert.DoesNotContain(f.Activities.Shared.Actor().ModuleProfileId.ToString(), serialized);
    }
    [LocalPostgresFact]
    public async Task SourceRevocationAtReadReleaseWithholdsPeopleAndClassrooms()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        await using (var db = await f.BoundOwner()) { db.AddRange(f.Evidence(), f.Evidence("Classroom")); await db.SaveChangesAsync(); }
        var reader = f.Reader((phase, _) => { if (phase == "read-release") f.Sources.Denied = true; return Task.CompletedTask; });
        var people = await f.People(reader).SearchAsync("visible"); Assert.NotEqual(LearningPeopleOutcome.Available, people.Outcome); Assert.Null(people.People);
        f.Sources.Denied = false; var classrooms = await f.Classrooms(reader).SearchAsync("visible"); Assert.NotEqual("Available", classrooms.Outcome); Assert.Null(classrooms.Classrooms);
    }
    [LocalPostgresFact]
    public async Task RestrictedBrowseRuntimeCannotWriteOrReadForeignScopeAndForceRlsRemains()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        await using (var owner = await f.BoundOwner()) { owner.Add(f.Evidence()); await owner.SaveChangesAsync(); }
        await using var runtime = f.Activities.Shared.OpenRuntime();
        await using var db = new IsolatedLearningBrowseDbContext(new DbContextOptionsBuilder<IsolatedLearningBrowseDbContext>().UseNpgsql(runtime.Database.GetConnectionString()!).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Activities.Shared.Owner.Subject.ToString()},false),set_config('andril.account',{Guid.NewGuid().ToString()},false),set_config('andril.module','learning',false)");
        Assert.Empty(await db.Set<LearningBrowseRow>().ToListAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning_browse_isolated.discovery_evidence','UPDATE') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning_browse_isolated.discovery_evidence','INSERT') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_learning_browse_isolated.discovery_evidence'::regclass").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
    }
}
