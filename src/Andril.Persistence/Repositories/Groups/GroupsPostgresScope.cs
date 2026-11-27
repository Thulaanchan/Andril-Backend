using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Groups;

public sealed class GroupsPostgresScope(AccountsDbContext accounts, DbContextOptions<GroupsDbContext> options,
    IPrivateProfileService profiles, ICurrentUser user, string runtimeRole)
{
    public Task<T> ExecuteAsync<T>(string module, Func<GroupsDbContext, Guid, CancellationToken, Task<T>> action, CancellationToken token) =>
        profiles.WithAuthorizedProfileAsync(module, (profile) => ExecuteCoreAsync(module, profile, action, token), token);

    private async Task<T> ExecuteCoreAsync<T>(string module, PrivateProfileDto profile,
        Func<GroupsDbContext, Guid, CancellationToken, Task<T>> action, CancellationToken token)
    {
        if (profile.Module != module || profile.ProfileId == Guid.Empty || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new ProfileAccessDeniedException();
        if (string.IsNullOrWhiteSpace(runtimeRole) || accounts.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction)
            throw new InvalidOperationException("Group operations require a current Accounts transaction and restricted database role.");

        var account = await accounts.Accounts.AsNoTracking().Where(row => row.SupabaseUserId == subject)
            .Select(row => row.Id).SingleOrDefaultAsync(token);
        if (account == Guid.Empty) throw new ProfileAccessDeniedException();

        await using var db = new GroupsDbContext(new DbContextOptionsBuilder<GroupsDbContext>(options)
            .UseNpgsql(accounts.Database.GetDbConnection()).Options);
        await db.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        const string savepoint = "andril_group_scope";
        await transaction.CreateSavepointAsync(savepoint, token);
        try
        {
            var previous = await db.Database.SqlQueryRaw<SessionScope>("""
                SELECT current_setting('role') AS "Role", coalesce(current_setting('andril.subject', true), '') AS "Subject",
                    coalesce(current_setting('andril.account', true), '') AS "Account", coalesce(current_setting('andril.module', true), '') AS "Module"
                """).SingleAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true), set_config('andril.account', {account.ToString()}, true),
                    set_config('andril.module', {module}, true), set_config('role', {runtimeRole}, true)
                """, token);
            var allowed = await db.Database.SqlQuery<bool>($"""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND row_security_active('andril_groups.groups'::regclass)
                    AND NOT EXISTS (SELECT FROM pg_class relation WHERE relation.oid = 'andril_groups.groups'::regclass
                        AND pg_has_role(current_user, relation.relowner, 'MEMBER'))
                    AND andril_groups.owns_current_profile({profile.ProfileId}, {module})) AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!allowed) throw new ProfileAccessDeniedException();

            var result = await action(db, profile.ProfileId, token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('role', {previous.Role}, true), set_config('andril.subject', {previous.Subject}, true),
                    set_config('andril.account', {previous.Account}, true), set_config('andril.module', {previous.Module}, true)
                """, token);
            await transaction.ReleaseSavepointAsync(savepoint, token);
            return result;
        }
        catch (Exception failure)
        {
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            if (failure is PostgresException { SqlState: "AG403" }) throw new ProfileAccessDeniedException();
            if (failure is PostgresException { SqlState: "AG409" }) throw new GroupQuotaReachedException();
            if (failure is PostgresException { SqlState: "AG503" }) throw new GroupAuthorityUnavailableException();
            throw;
        }
    }

    private sealed record SessionScope(string Role, string Subject, string Account, string Module);
}

public sealed class GroupQuotaReachedException() : InvalidOperationException("The group creation quota is reached.");
public sealed class GroupAuthorityUnavailableException() : InvalidOperationException("Current group authority is unavailable.");
