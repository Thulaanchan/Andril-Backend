using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Interfaces;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsReadPostgresTests
{
    [LocalPostgresFact]
    public async Task DiscoveryIsBoundedAndFiltersPrivateUndiscoverableAndBlockedGroups()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var caller = await fixture.SeedAsync("hobbies");
        var host = await fixture.SeedAsync("hobbies");
        var social = await fixture.SeedAsync("social");
        var clubs = new GroupModuleScope("hobbies", GroupKind.Club);
        var publicGroup = await fixture.CreateGroupAsync(host, clubs, new("Needle Club", null, true, "public"));
        await fixture.CreateGroupAsync(host, clubs, new("Needle Private", null, true));
        await fixture.CreateGroupAsync(host, clubs, new("Needle Unlisted", null, false, "public"));
        await fixture.CreateGroupAsync(host, clubs, new("Unrelated Club", null, true, "public"));
        await fixture.CreateGroupAsync(social, new("social", GroupKind.Zone), new("Needle Zone", null, true, "public"));
        var query = new GroupDiscoveryQuery(caller.Profile, clubs, "needle", 1);
        Assert.Equal(publicGroup.Id, Assert.Single((await Discover(query))!).Group.Id);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Discover(query with { ActorProfileId = host.Profile }));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Discover(query with { MaximumCandidates = 101 }));
        await new PostgresGroupAuthorityWriter(fixture.Options(), host, host, fixture.Role).BlockAsync("hobbies", caller.Profile, true);
        Assert.Empty((await Discover(query))!);

        Task<IReadOnlyList<GroupDiscoveryEvidence>?> Discover(GroupDiscoveryQuery input) => fixture.WithScopeAsync(caller, execution =>
        {
            var repository = new PostgresGroupReadRepository(execution);
            return repository.WithAuthorizedReadAsync<IReadOnlyList<GroupDiscoveryEvidence>>(input, async (snapshot, ct) =>
                await repository.RevalidateAsync(input, snapshot, ct) == GroupDiscoveryDecision.Allowed ? snapshot.Groups : null);
        });
    }

    [LocalPostgresFact]
    public async Task PersonalFactsRequireTheExactCallbackAndDistinguishDeniedFromVerifiedAbsence()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        var applicant = await fixture.SeedAsync("hobbies");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var group = await fixture.CreateGroupAsync(host, scope, new("Public Club", null, true, "public"));
        var query = new GroupPersonalParticipationQuery(applicant.Profile, group.Id, scope);

        await fixture.WithScopeAsync(applicant, async execution =>
        {
            var repository = new PostgresGroupReadRepository(execution);
            var snapshot = await repository.WithAuthorizedReadAsync<GroupPersonalParticipationSnapshot>(query, async (value, ct) =>
            {
                Assert.Equal(GroupPersonalParticipationDecision.Denied, await repository.RevalidateAsync(query with { }, value, ct));
                Assert.Equal(GroupPersonalParticipationDecision.Allowed, await repository.RevalidateAsync(query, value, ct));
                return value;
            });
            Assert.NotNull(snapshot);
            Assert.Equal(GroupPersonalParticipationPresence.Absent, snapshot.Membership.Presence);
            Assert.Equal(GroupPersonalParticipationPresence.Absent, snapshot.OutstandingJoinRequest.Presence);
            Assert.Equal(GroupPersonalParticipationDecision.Denied, await repository.RevalidateAsync(query, snapshot));
            Assert.Null(await repository.WithAuthorizedReadAsync(query, (value, _) => Task.FromResult<GroupPersonalParticipationSnapshot?>(value)));
            return snapshot;
        });

        await fixture.WithScopeAsync(applicant, execution => new PostgresGroupMembershipTransactionRepository(execution)
            .SubmitJoinRequestAtomicAsync(group.Id, scope, applicant.Profile, DateTime.UtcNow));
        Assert.Equal(GroupPersonalParticipationPresence.Present, (await Read())!.OutstandingJoinRequest.Presence);
        await new PostgresGroupAuthorityWriter(fixture.Options(), applicant, applicant, fixture.Role).BlockAsync("hobbies", host.Profile, true);
        Assert.Null(await Read());

        Task<GroupPersonalParticipationSnapshot?> Read() => fixture.WithScopeAsync(applicant, execution =>
        {
            var repository = new PostgresGroupReadRepository(execution);
            return repository.WithAuthorizedReadAsync<GroupPersonalParticipationSnapshot>(query, async (snapshot, ct) =>
                await repository.RevalidateAsync(query, snapshot, ct) == GroupPersonalParticipationDecision.Allowed ? snapshot : null);
        });
    }

    [LocalPostgresFact]
    public async Task DiscoveryHoldsTheDatabaseAuthorizationFenceUntilTheCallbackReturns()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var caller = await fixture.SeedAsync("hobbies");
        var host = await fixture.SeedAsync("hobbies");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var group = await fixture.CreateGroupAsync(host, scope, new("Club", null, true, "public"));
        var query = new GroupDiscoveryQuery(caller.Profile, scope, "Club", 5);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = fixture.WithScopeAsync(caller, execution =>
        {
            var repo = new PostgresGroupReadRepository(execution);
            return repo.WithAuthorizedReadAsync<IReadOnlyList<GroupDiscoveryEvidence>>(query, async (snapshot, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return await repo.RevalidateAsync(query, snapshot, ct) == GroupDiscoveryDecision.Allowed ? snapshot.Groups : null;
            }, timeout.Token);
        });

        await entered.Task.WaitAsync(timeout.Token);
        var block = new PostgresGroupAuthorityWriter(fixture.Options(), host, host, fixture.Role)
            .BlockAsync("hobbies", caller.Profile, true, timeout.Token);
        try
        {
            await using var observer = fixture.Open();
            while (!await observer.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """).SingleAsync(timeout.Token))
            {
                if (block.IsCompleted) throw new InvalidOperationException("Block completed before discovery released authorization.");
                await Task.Delay(20, timeout.Token);
            }
        }
        finally { release.TrySetResult(); }

        Assert.Equal(group.Id, Assert.Single((await read)!).Group.Id);
        await block;
        var after = await fixture.WithScopeAsync(caller, execution =>
        {
            var repo = new PostgresGroupReadRepository(execution);
            return repo.WithAuthorizedReadAsync<IReadOnlyList<GroupDiscoveryEvidence>>(query, async (snapshot, ct) =>
                await repo.RevalidateAsync(query, snapshot, ct) == GroupDiscoveryDecision.Allowed ? snapshot.Groups : null);
        });
        Assert.Empty(after!);
    }

    [LocalPostgresFact]
    public async Task AnUnreadableRestrictionAuthorityCannotReleaseVerifiedDiscovery()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var caller = await fixture.SeedAsync("hobbies");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        await using var administrator = fixture.Open();
        var privileges = $"""
            GRANT SELECT ON andril_groups.module_restrictions TO {fixture.Role};
            GRANT EXECUTE ON FUNCTION andril_groups.safety_source_visible(regclass, text) TO {fixture.Role};
            ALTER FUNCTION andril_groups.profile_is_restricted(uuid, text) OWNER TO {fixture.Role};
            """;
        await administrator.Database.ExecuteSqlRawAsync(privileges);
        var query = new GroupDiscoveryQuery(caller.Profile, scope, "", 1);
        await Assert.ThrowsAsync<GroupAuthorityUnavailableException>(() => fixture.WithScopeAsync(caller, execution =>
            new PostgresGroupReadRepository(execution).WithAuthorizedReadAsync<object>(query,
                (_, _) => throw new InvalidOperationException("Unverified authority released a callback."))));
    }
}
