using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Social;

/// <summary>Executes scope-bound Social safety changes on the authoritative PostgreSQL store.</summary>
public sealed class PostgresSocialSafetyRepository(DbContextOptions<SocialDbContext> options, ICurrentUser user,
    IOnboardingIdentity identity, string runtimeRole)
{
    public Task SetBlockAsync(Guid target, bool blocked, CancellationToken token = default) => ExecuteAsync(target,
        db => db.Database.SqlQuery<bool>($"SELECT andril_social.set_social_block({target}, {blocked}) AS \"Value\""), token);

    public Task SetRestrictionAsync(Guid target, bool restricted, CancellationToken token = default) => ExecuteAsync(target,
        db => db.Database.SqlQuery<bool>($"SELECT andril_social.set_social_restriction({target}, {restricted}) AS \"Value\""), token);

    private async Task ExecuteAsync(Guid target, Func<SocialDbContext, IQueryable<bool>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target == Guid.Empty || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty
            || !identity.IsConfirmedFor(subject)) throw new ProfileAccessDeniedException();

        await using var db = new SocialDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true), set_config('andril.module', 'social', true),
                    set_config('role', {runtimeRole}, true)
                """, token);
            var restrictedRole = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND row_security_active('andril_social.social_profile_blocks'::regclass)
                    AND NOT has_table_privilege('andril_social.social_profile_blocks', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege('andril_social.social_profile_restrictions', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege('andril_social.social_moderation_grants', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')) AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!restrictedRole) throw new InvalidOperationException("The Social safety database role must be restricted.");
            _ = await operation(db).SingleAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new ProfileAccessDeniedException();
        }
    }
}
