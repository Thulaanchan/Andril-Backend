using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsMembershipPostgresTests
{
    private static readonly GroupModuleScope Zone = new("social", GroupKind.Zone);

    [LocalPostgresFact]
    public async Task PendingRequestsHaveExactOwnersAndOnlyAHostCanResolveThem()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("social");
        var applicant = await fixture.SeedAsync("social");
        var outsider = await fixture.SeedAsync("social");
        var group = await fixture.CreateGroupAsync(host, Zone, new("Zone", null, true, "public"));
        var submitted = await Run(applicant, repository => repository.SubmitJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile, DateTime.UtcNow));
        Assert.True(submitted.Success);
        var duplicate = await Run(applicant, repository => repository.SubmitJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile, DateTime.UtcNow));
        Assert.Equal(GroupMembershipTransactionStatus.RequestAlreadyPending, duplicate.Status);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Run(outsider,
            repository => repository.CancelJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Run(outsider,
            repository => repository.ProcessAdmissionAtomicAsync(group.Id, Zone, applicant.Profile, true, outsider.Profile, DateTime.UtcNow)));
        var conflict = await Run(applicant, repository => repository.CancelJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile, group.Revision));
        Assert.Equal(GroupMembershipTransactionStatus.ConcurrencyConflict, conflict.Status);
        Assert.True((await Run(applicant, repository => repository.CancelJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile))).RequestCancelled);
        Assert.True((await Run(applicant, repository => repository.SubmitJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile, DateTime.UtcNow))).Success);
        var rejected = await Run(host, repository => repository.ProcessAdmissionAtomicAsync(group.Id, Zone, applicant.Profile, false, host.Profile, DateTime.UtcNow));
        Assert.True(rejected.RequestResolved);
        Assert.False(rejected.MembershipCreated);
        Assert.Equal(1L, await fixture.CountAsync("memberships"));

        Task<T> Run<T>(SharedGroupsDatabase.Actor actor, Func<PostgresGroupMembershipTransactionRepository, Task<T>> operation) =>
            fixture.WithScopeAsync(actor, scope => operation(new(scope)));
    }

    [LocalPostgresFact]
    public async Task ConcurrentApprovalCreatesOneMembershipAndDeparturePersistsAcrossContexts()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("social");
        var applicant = await fixture.SeedAsync("social");
        var group = await fixture.CreateGroupAsync(host, Zone, new("Zone", null, true, "public"));
        await fixture.WithScopeAsync(applicant, scope => new PostgresGroupMembershipTransactionRepository(scope)
            .SubmitJoinRequestAtomicAsync(group.Id, Zone, applicant.Profile, DateTime.UtcNow));
        Task<GroupAdmissionTransactionResult> Approve() => fixture.WithScopeAsync(host, scope =>
            new PostgresGroupMembershipTransactionRepository(scope).ProcessAdmissionAtomicAsync(group.Id, Zone, applicant.Profile, true, host.Profile, DateTime.UtcNow));
        Assert.Single(await Task.WhenAll(Approve(), Approve()), result => result.MembershipCreated);
        Assert.Equal(2L, await fixture.CountAsync("memberships"));
        var departure = await fixture.WithScopeAsync(applicant,
            scope => new PostgresGroupMembershipTransactionRepository(scope).LeaveGroupAtomicAsync(group.Id, Zone, applicant.Profile));
        Assert.True(departure.MembershipRemoved);
        await using var context = fixture.Open();
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM andril_groups.memberships WHERE departed_at_utc IS NULL
            """).SingleAsync());
    }
}
