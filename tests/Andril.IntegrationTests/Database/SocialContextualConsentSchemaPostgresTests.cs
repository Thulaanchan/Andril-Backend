using Andril.AI.Policies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialContextualConsentSchemaPostgresTests
{
    [LocalPostgresFact]
    public async Task ConsentAuditHasAnExclusiveExpiryAndIdempotentHoldAwareCleanup()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var actor = await fixture.SeedAsync();
        await using var db = fixture.Database.Open();
        var operation = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var held = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social_ai.consents(operation_id, account_id, profile_id, conversation_id, session_epoch, module_epoch,
                purpose_id, purpose_version, scope_id, notice_revision, state, revision, offered_at_utc)
            VALUES ({operation}, {actor.Account}, {actor.Profile}, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
                'fixture', 'v1', 'self', 'notice', {(int)AiConsentState.AwaitingConsent}, gen_random_uuid(), clock_timestamp());
            INSERT INTO andril_social_ai.consent_decision_audit(event_id, operation_id, decision, purpose_version, occurred_at_utc) VALUES
                ({recent}, {operation}, 1, 'v1', clock_timestamp()),
                ({expired}, {operation}, 2, 'v1', clock_timestamp() - interval '336 hours'),
                ({held}, {operation}, 2, 'v1', clock_timestamp() - interval '337 hours');
            INSERT INTO andril_social_ai.consent_audit_holds(event_id) VALUES ({held});
            """);

        var visible = await db.Database.SqlQueryRaw<Guid>("""
            SELECT event_id AS "Value" FROM andril_social_ai.read_current_consent_audit()
            """).ToListAsync();
        Assert.Equal(recent, Assert.Single(visible));
        const string purge = """SELECT andril_social_ai.purge_expired_consent_audit() AS "Value" """;
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(purge).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(purge).SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM andril_social_ai.consent_decision_audit""").SingleAsync());

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role', {fixture.Role}, true)");
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT NOT has_schema_privilege(current_user, 'andril_social_ai', 'USAGE')
                AND NOT EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname = 'andril_social_ai' AND p.proname = 'read_current_consent_audit'
                      AND has_function_privilege(current_user, p.oid, 'EXECUTE')) AS "Value"
            """).SingleAsync());
        await transaction.RollbackAsync();

        Assert.Equal(5, await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname='andril_social_ai' AND t.relkind='r' AND t.relrowsecurity AND t.relforcerowsecurity
                AND t.relname IN ('consents','consent_requests','consent_invalidations','consent_decision_audit','consent_audit_holds')
            """).SingleAsync());
    }
}
