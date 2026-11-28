using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Groups;

public sealed class PostgresGroupAuthorityWriter(DbContextOptions<GroupsDbContext> options, ICurrentUser user,
    IOnboardingIdentity identity, string runtimeRole)
{
    public Task BlockAsync(string module, Guid target, bool enabled, CancellationToken token = default) =>
        WriteAsync(module, target, "block", enabled, token);

    public Task RestrictAsync(string module, Guid target, bool enabled, CancellationToken token = default) =>
        WriteAsync(module, target, "restrict", enabled, token);

    public Task SetClassroomHostAsync(Guid target, bool enabled, CancellationToken token = default) =>
        WriteAsync("learning", target, "educator", enabled, token);

    private async Task WriteAsync(string module, Guid target, string operation, bool enabled, CancellationToken token)
    {
        if (module is not ("learning" or "hobbies") || target == Guid.Empty || !user.IsAuthenticated || user.UserId is not Guid subject
            || subject == Guid.Empty || !identity.IsConfirmedFor(subject)) throw new ProfileAccessDeniedException();
        await using var db = new GroupsDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true), set_config('role', {runtimeRole}, true)
                """, token);
            var restricted = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND row_security_active('andril_groups.module_blocks'::regclass)
                    AND NOT has_table_privilege('andril_groups.moderator_grants', 'INSERT,UPDATE,DELETE,TRUNCATE')) AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!restricted) throw new GroupAuthorityUnavailableException();
            _ = await db.Database.SqlQuery<bool>($"SELECT andril_groups.write_authority({module}, {target}, {operation}, {enabled}) AS \"Value\"")
                .SingleAsync(token);
            await tx.CommitAsync(token);
        }
        catch (PostgresException error) when (error.SqlState == "AG403") { throw new ProfileAccessDeniedException(); }
    }
}
