using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsCanonicalEntitlementPostgresTests
{
    [LocalPostgresFact]
    public async Task CanonicalMembershipCapacityTracksFreeGrantExpiryRevocationAndRenewal()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var creator = await fixture.SeedAsync("hobbies");
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var club = await fixture.CreateGroupAsync(creator, scope, new("Club", null, false));
        Assert.Equal(50, await Capacity());
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(100, await Capacity());
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(50, await Capacity());
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(100, await Capacity());
        await using var authority = fixture.Open();
        await authority.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_economy.premium_entitlements
            SET tier = 0, is_active = false, expires_at_utc = NULL, updated_at_utc = clock_timestamp()
            WHERE account_id = {creator.Account}
            """);
        Assert.Equal(50, await Capacity());

        Task<int?> Capacity() => fixture.WithScopeAsync(creator, execution =>
            new PostgresGroupEvidenceProvider(execution).GetGroupCapacityAsync(club.Id, scope));
    }
}
