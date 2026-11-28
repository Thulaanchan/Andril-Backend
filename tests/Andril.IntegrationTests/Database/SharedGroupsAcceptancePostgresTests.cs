using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Groups;
using Andril.Persistence.Repositories.Social;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsAcceptancePostgresTests
{
    private static readonly GroupModuleScope Club = new("hobbies", GroupKind.Club);

    [LocalPostgresFact]
    public async Task ClubAdmissionsEnforceTheLastFreeAndPremiumSlotsAndDoNotEvictOnExpiry()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        var first = await fixture.SeedAsync("hobbies");
        var second = await fixture.SeedAsync("hobbies");
        var third = await fixture.SeedAsync("hobbies");
        var fourth = await fixture.SeedAsync("hobbies");
        var group = await fixture.CreateGroupAsync(host, Club, new("Capacity", null, true, "public"));

        await fixture.SeedExistingClubMembersAsync(group.Id, 48);
        await SubmitAsync(first);
        await SubmitAsync(second);
        var free = await Task.WhenAll(ApproveAsync(first), ApproveAsync(second));
        Assert.Single(free, result => result.MembershipCreated);
        Assert.Single(free, result => result.Status == GroupMembershipTransactionStatus.CapacityExceeded);
        Assert.Equal(50L, await fixture.CountAsync("memberships"));

        await fixture.SetPremiumAsync(host, DateTimeOffset.UtcNow.AddDays(1));
        await fixture.SeedExistingClubMembersAsync(group.Id, 49);
        await SubmitAsync(third);
        await SubmitAsync(fourth);
        var premium = await Task.WhenAll(ApproveAsync(third), ApproveAsync(fourth));
        Assert.Single(premium, result => result.MembershipCreated);
        Assert.Single(premium, result => result.Status == GroupMembershipTransactionStatus.CapacityExceeded);
        Assert.Equal(100L, await fixture.CountAsync("memberships"));

        await fixture.SetPremiumAsync(host, DateTimeOffset.UtcNow.AddDays(-1));
        var waiting = premium[0].MembershipCreated ? fourth : third;
        Assert.Equal(GroupMembershipTransactionStatus.CapacityExceeded, (await ApproveAsync(waiting)).Status);
        Assert.Equal(100L, await fixture.CountAsync("memberships"));

        Task<GroupJoinRequestSubmissionTransactionResult> SubmitAsync(SharedGroupsDatabase.Actor applicant) =>
            fixture.WithScopeAsync(applicant, execution => new PostgresGroupMembershipTransactionRepository(execution)
                .SubmitJoinRequestAtomicAsync(group.Id, Club, applicant.Profile, DateTime.UtcNow));
        Task<GroupAdmissionTransactionResult> ApproveAsync(SharedGroupsDatabase.Actor applicant) =>
            fixture.WithScopeAsync(host, execution => new PostgresGroupMembershipTransactionRepository(execution)
                .ProcessAdmissionAtomicAsync(group.Id, Club, applicant.Profile, true, host.Profile, DateTime.UtcNow));
    }

    [LocalPostgresFact]
    public async Task CancellationDuringTheQuotaFenceRollsBackCreationAndDoesNotPoisonTheNextTransaction()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        await using var writer = fixture.Open();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("LOCK TABLE andril_economy.premium_entitlements IN ROW EXCLUSIVE MODE");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creation = fixture.WithEntriesAsync(host, entry =>
            entry.CreateAsync(Club, new("Cancelled", null, false), cancellation.Token));
        await using var observer = fixture.Open();
        while (!await observer.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT FROM pg_locks where relation = 'andril_economy.premium_entitlements'::regclass
                AND mode = 'ShareLock' AND NOT granted) AS "Value"
            """).SingleAsync(cancellation.Token))
        {
            if (creation.IsCompleted) throw new InvalidOperationException("Creation finished before fencing the canonical writer.");
            await Task.Delay(20, cancellation.Token);
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        await transaction.CommitAsync();
        Assert.Equal(0L, await fixture.CountAsync("groups"));
        Assert.Equal(0L, await fixture.CountAsync("memberships"));
        await fixture.CreateGroupAsync(host, Club, new("Committed after cancellation", null, false));
        Assert.Equal(1L, await fixture.CountAsync("groups"));
    }

    [LocalPostgresFact]
    public async Task TheCanonicalInitialGrantCanCommitWhileCreationWaitsOnItsEntitlementLock()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        for (var index = 0; index < 5; index++)
            await fixture.CreateGroupAsync(host, Club, new("Before grant", null, false));

        await using var writer = fixture.Open();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("LOCK TABLE andril_economy.premium_entitlements IN ROW EXCLUSIVE MODE");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creation = fixture.WithEntriesAsync(host, entry =>
            entry.CreateAsync(Club, new("After canonical grant", null, false), deadline.Token));
        await using var observer = fixture.Open();
        while (!await observer.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT FROM pg_locks WHERE relation = 'andril_economy.premium_entitlements'::regclass
                AND mode = 'ShareLock' AND NOT granted) AS "Value"
            """).SingleAsync(deadline.Token))
        {
            if (creation.IsCompleted) throw new InvalidOperationException("Creation finished before observing the current entitlement.");
            await Task.Delay(20, deadline.Token);
        }

        await writer.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_economy.premium_entitlements(account_id, tier, is_active, expires_at_utc, provider,
                external_subscription_id, created_at_utc, updated_at_utc)
            VALUES ({host.Account}, 1, true, CURRENT_TIMESTAMP + interval '1 day', 'isolated-fixture', null, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (account_id) DO UPDATE SET tier = EXCLUDED.tier, is_active = EXCLUDED.is_active,
                expires_at_utc = EXCLUDED.expires_at_utc, updated_at_utc = EXCLUDED.updated_at_utc
            """, deadline.Token);
        await transaction.CommitAsync(deadline.Token);
        Assert.Equal("After canonical grant", (await creation).Name);
        Assert.Equal(6L, await fixture.CountAsync("groups"));
    }

    [LocalPostgresFact]
    public async Task ExistingSocialSafetyWriterSuppressesZoneDiscoveryAndPendingAdmission()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("social");
        var applicant = await fixture.SeedAsync("social");
        var zone = new GroupModuleScope("social", GroupKind.Zone);
        var group = await fixture.CreateGroupAsync(host, zone, new("Zone", null, true, "public"));
        await using var admin = fixture.Open();
        var grant = $"""
            GRANT USAGE ON SCHEMA andril_social TO {fixture.Role};
            GRANT EXECUTE ON FUNCTION andril_social.set_social_block(uuid, boolean) TO {fixture.Role}
            """;
        await admin.Database.ExecuteSqlRawAsync(grant);
        var options = new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.Database.ConnectionString).Options;
        var original = new PostgresSocialSafetyRepository(options, host, host, fixture.Role);
        await fixture.WithScopeAsync(applicant, execution => new PostgresGroupMembershipTransactionRepository(execution)
            .SubmitJoinRequestAtomicAsync(group.Id, zone, applicant.Profile, DateTime.UtcNow));
        var query = new GroupDiscoveryQuery(applicant.Profile, zone, "Zone", 5);
        Assert.Single((await Search())!);
        await original.SetBlockAsync(applicant.Profile, true);
        Assert.Empty((await Search())!);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.WithScopeAsync(host, execution =>
            new PostgresGroupMembershipTransactionRepository(execution).ProcessAdmissionAtomicAsync(group.Id, zone,
                applicant.Profile, true, host.Profile, DateTime.UtcNow)));
        Assert.Equal(1L, await fixture.CountAsync("memberships"));
        await original.SetBlockAsync(applicant.Profile, false);
        Assert.Single((await Search())!);
        Assert.Equal(1L, await fixture.CountAsync("memberships"));

        Task<IReadOnlyList<GroupDiscoveryEvidence>?> Search() => fixture.WithScopeAsync(applicant, execution =>
        {
            var reader = new PostgresGroupReadRepository(execution);
            return reader.WithAuthorizedReadAsync<IReadOnlyList<GroupDiscoveryEvidence>>(query, async (snapshot, ct) =>
                await reader.RevalidateAsync(query, snapshot, ct) == GroupDiscoveryDecision.Allowed ? snapshot.Groups : null);
        });
    }

    [LocalPostgresFact]
    public async Task InactiveProfilesAndMinorsCannotCreateZonesOrUseOtherModuleProfiles()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var minor = await fixture.SeedAsync("social", minor: true);
        var inactive = await fixture.SeedAsync("social", active: false);
        var hobbies = await fixture.SeedAsync("hobbies");
        var zone = new GroupModuleScope("social", GroupKind.Zone);
        var settings = new GroupSettings("Denied Zone", null, true, "public");
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.CreateGroupAsync(minor, zone, settings));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.CreateGroupAsync(inactive, zone, settings));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.CreateGroupAsync(hobbies, zone, settings));
        Assert.Equal(0L, await fixture.CountAsync("groups"));
    }
}
