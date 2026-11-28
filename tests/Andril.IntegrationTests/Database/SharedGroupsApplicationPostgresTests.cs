using Andril.API.Authorization.Groups;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Andril.Persistence.Repositories.Groups;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsApplicationPostgresTests
{
    [LocalPostgresFact]
    public async Task ApplicationUsesScopedOpaqueRequestsForAdmissionAndOwnerOnlyStatus()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        var applicant = await fixture.SeedAsync("hobbies");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var group = await Run(host, (management, _) => management.CreateAsync(scope, new("Club", null, true, "public"), default));
        var cards = await Run(applicant, (management, _) => management.DiscoverAsync(scope, "Club", 10, default));
        Assert.Single(cards!);
        Assert.True((await Run(applicant, (_, membership) => membership.SubmitJoinRequestStrictAsync(group.Id, scope))).Success);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Run(applicant,
            (management, _) => management.ReadPendingAsync(group.Id, scope, 10, default)));
        var old = Assert.Single(await Run(host, (management, _) => management.ReadPendingAsync(group.Id, scope, 10, default)));
        Assert.NotEqual(applicant.Profile, old.RequestId);
        Assert.True((await Run(applicant, (_, membership) => membership.CancelJoinRequestStrictAsync(group.Id, scope))).Success);
        Assert.True((await Run(applicant, (_, membership) => membership.SubmitJoinRequestStrictAsync(group.Id, scope))).Success);
        Assert.False((await Run(host, (management, _) => management.DecideAsync(group.Id, scope, old.RequestId, true, default))).Success);
        var current = Assert.Single(await Run(host, (management, _) => management.ReadPendingAsync(group.Id, scope, 10, default)));
        Assert.True((await Run(host, (management, _) => management.DecideAsync(group.Id, scope, current.RequestId, true, default))).MembershipCreated);
        Assert.NotNull(await Run(applicant, (management, _) => management.GetOwnStatusAsync(group.Id, scope, default)));
        Assert.True((await Run(applicant, (_, membership) => membership.LeaveGroupStrictAsync(group.Id, scope))).EntityRemoved);

        async Task<T> Run<T>(SharedGroupsDatabase.Actor actor, Func<GroupManagementService, GroupMembershipOperationService, Task<T>> operation)
        {
            await using var accounts = fixture.Database.OpenAccounts();
            var profileService = new SharedGroupsDatabase.FixtureProfileService(new Andril.Persistence.Repositories.Common.PrivateProfileRepository(accounts), actor.Account);
            var execution = new GroupsPostgresScope(accounts, fixture.Options(), profileService, actor, fixture.Role);
            var evidence = new PostgresGroupEvidenceProvider(execution);
            var actorContext = new GroupActorContext(profileService, evidence);
            var reader = new PostgresGroupReadRepository(execution);
            var membership = new GroupMembershipOperationService(new PostgresGroupMembershipTransactionRepository(execution), evidence, actorContext);
            var management = new GroupManagementService(new(execution), new(execution),
                new GroupDiscoveryAccessService(actorContext, new GroupDiscoveryService(reader)),
                new GroupPersonalParticipationAccessService(actorContext, new GroupPersonalParticipationService(reader)), membership);
            return await operation(management, membership);
        }
    }
}
