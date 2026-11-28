using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsAuthorityPostgresTests
{
    [LocalPostgresFact]
    public async Task OnlyScopedModeratorsGrantEducatorAuthorityAndRevocationStopsHostActions()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var moderator = await fixture.SeedAsync("learning");
        var educator = await fixture.SeedAsync("learning");
        var learner = await fixture.SeedAsync("learning");
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        await using var db = fixture.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_groups.moderator_grants(account_id, module) VALUES ({moderator.Account}, 'learning')
            """);
        PostgresGroupAuthorityWriter Authority(SharedGroupsDatabase.Actor actor) => new(fixture.Options(), actor, actor, fixture.Role);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Authority(educator).SetClassroomHostAsync(educator.Profile, true));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Authority(moderator).SetClassroomHostAsync(moderator.Profile, true));
        await Authority(moderator).SetClassroomHostAsync(educator.Profile, true);
        var group = await fixture.CreateGroupAsync(educator, scope, new("Classroom", null, true, "public"));
        await fixture.WithScopeAsync(learner, execution => new PostgresGroupMembershipTransactionRepository(execution)
            .SubmitJoinRequestAtomicAsync(group.Id, scope, learner.Profile, DateTime.UtcNow));
        await Authority(moderator).SetClassroomHostAsync(educator.Profile, false);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.WithScopeAsync(educator, execution =>
            new PostgresGroupMembershipTransactionRepository(execution).ProcessAdmissionAtomicAsync(group.Id, scope,
                learner.Profile, true, educator.Profile, DateTime.UtcNow)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Authority(moderator).RestrictAsync("hobbies", learner.Profile, true));
        await Authority(moderator).RestrictAsync("learning", learner.Profile, true);
        Assert.Null(await fixture.WithScopeAsync(learner, execution => new PostgresGroupEvidenceProvider(execution)
            .GetProfileEvidenceAsync(learner.Profile, "learning")));
        Assert.Equal(1L, await fixture.CountAsync("memberships"));
    }

    [LocalPostgresFact]
    public async Task OwnDirectionalBlocksFenceAdmissionAndNeverRemoveExistingMemberships()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        var applicant = await fixture.SeedAsync("hobbies");
        var wrongModule = await fixture.SeedAsync("learning");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var group = await fixture.CreateGroupAsync(host, scope, new("Club", null, true, "public"));
        PostgresGroupAuthorityWriter Authority(SharedGroupsDatabase.Actor actor) => new(fixture.Options(), actor, actor, fixture.Role);
        var request = await fixture.WithScopeAsync(applicant, execution => new PostgresGroupMembershipTransactionRepository(execution)
            .SubmitJoinRequestAtomicAsync(group.Id, scope, applicant.Profile, DateTime.UtcNow));
        Assert.True(request.Success);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Authority(host).BlockAsync("hobbies", wrongModule.Profile, true));
        await Authority(applicant).BlockAsync("hobbies", host.Profile, true);
        await Authority(host).BlockAsync("hobbies", applicant.Profile, false);
        var approve = () => fixture.WithScopeAsync(host, execution => new PostgresGroupMembershipTransactionRepository(execution)
            .ProcessAdmissionAtomicAsync(group.Id, scope, applicant.Profile, true, host.Profile, DateTime.UtcNow));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(approve);
        await Authority(applicant).BlockAsync("hobbies", host.Profile, false);
        Assert.True((await approve()).MembershipCreated);
        await Authority(host).BlockAsync("hobbies", applicant.Profile, true);
        Assert.Equal(2L, await fixture.CountAsync("memberships"));
    }
}
