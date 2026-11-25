using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.IntegrationTests.Database;

/// <summary>Owner-only provisioning in the existing disposable M2 fixture; never runtime authority.</summary>
internal static class LearningPostgresFixture
{
    internal static LearningDbContext Open(LocalAccountDatabase fixture)
    {
        using var accounts = fixture.Open();
        return new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(accounts.Database.GetConnectionString()).Options);
    }

    internal static async Task Migrate(LocalAccountDatabase fixture)
    {
        await using var db = Open(fixture);
        await db.Database.MigrateAsync();
    }

    internal static async Task Profile(LocalAccountDatabase fixture, LearningSetupScope scope, Guid? subject = null,
        string module = "learning", bool active = true)
    {
        await using var db = Open(fixture);
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = subject ?? scope.AccountId;
        // FORCE RLS applies to the fixture owner too. Reuse M2's authoritative session keys
        // within this provisioning transaction; no owner-bypass policy or runtime grant.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{actor.ToString()},true),set_config('andril.account',{scope.AccountId.ToString()},true),set_config('andril.module',{module},true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.accounts(id,supabase_user_id,created_at_utc,updated_at_utc) VALUES({scope.AccountId},{actor},CURRENT_TIMESTAMP,CURRENT_TIMESTAMP) ON CONFLICT(id) DO NOTHING");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.account_module_lifecycles(account_id,module,state,revision,first_activated_at_utc,state_changed_at_utc) VALUES({scope.AccountId},{module},{(active ? "active" : "inactive")},1,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP) ON CONFLICT(account_id,module) DO NOTHING");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.private_module_profiles(id,account_id,module,created_at_utc) VALUES({scope.LearningProfileId},{scope.AccountId},{module},CURRENT_TIMESTAMP) ON CONFLICT(id) DO NOTHING");
        await tx.CommitAsync();
    }
}
