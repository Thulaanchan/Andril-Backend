using System.Data.Common;
using Andril.API.Authorization.Groups;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Interfaces;
using Andril.Groups.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Groups;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Andril.API.Extensions;

public static class SharedGroupsRuntimeExtensions
{
    public static IServiceCollection AddVerifiedSharedGroupsPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("SharedGroups:EnablePersistence")) return services;
        var role = configuration["SharedGroups:RuntimeRole"];
        var connection = configuration.GetConnectionString("AndrilAccounts");
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Shared Groups requires an Accounts connection and a restricted runtime role.");

        NpgsqlConnectionStringBuilder settings;
        try { settings = new(connection) { IncludeErrorDetail = false, PersistSecurityInfo = false }; }
        catch (ArgumentException) { throw new InvalidOperationException("The Shared Groups database configuration is invalid."); }
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException("The Shared Groups database configuration is invalid.");

        var modules = new HashSet<string>(StringComparer.Ordinal);
        if (configuration.GetValue<bool>("SharedGroups:EnableLearning")) modules.Add("learning");
        if (configuration.GetValue<bool>("SharedGroups:EnableHobbies")) modules.Add("hobbies");
        if (configuration.GetValue<bool>("SharedGroups:EnableSocial")) modules.Add("social");
        if (modules.Count == 0) throw new InvalidOperationException("Enable at least one explicit Shared Groups module.");

        services.AddSingleton(new GroupsRuntime(role, modules));
        services.AddDbContext<GroupsDbContext>(options => options.UseNpgsql(settings.ConnectionString, provider =>
            provider.MigrationsHistoryTable(GroupsDbContextFactory.MigrationsHistoryTableName, GroupsDbContext.Schema)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped(provider => new GroupsPostgresScope(provider.GetRequiredService<AccountsDbContext>(),
            provider.GetRequiredService<DbContextOptions<GroupsDbContext>>(), provider.GetRequiredService<IPrivateProfileService>(),
            provider.GetRequiredService<ICurrentUser>(), role, modules));
        services.TryAddScoped(provider => new PostgresGroupAuthorityWriter(provider.GetRequiredService<DbContextOptions<GroupsDbContext>>(),
            provider.GetRequiredService<ICurrentUser>(), provider.GetRequiredService<IOnboardingIdentity>(), role, modules));
        services.TryAddScoped<PostgresGroupEntryRepository>();
        services.TryAddScoped<IGroupEntryRepository>(provider => provider.GetRequiredService<PostgresGroupEntryRepository>());
        services.TryAddScoped<IGroupAuthoritativeEvidenceProvider, PostgresGroupEvidenceProvider>();
        services.TryAddScoped<IGroupMembershipTransactionRepository, PostgresGroupMembershipTransactionRepository>();
        services.TryAddScoped<PostgresGroupReadRepository>();
        services.TryAddScoped<IGroupDiscoveryReadRepository>(provider => provider.GetRequiredService<PostgresGroupReadRepository>());
        services.TryAddScoped<IGroupPersonalParticipationReadRepository>(provider => provider.GetRequiredService<PostgresGroupReadRepository>());
        services.TryAddScoped<PostgresGroupHostRequests>();
        services.TryAddScoped(provider => new GroupMembershipOperationService(
            provider.GetRequiredService<IGroupMembershipTransactionRepository>(),
            provider.GetRequiredService<IGroupAuthoritativeEvidenceProvider>(), provider.GetRequiredService<IGroupActorContext>()));
        services.TryAddScoped<IGroupMembershipOperationService>(provider => provider.GetRequiredService<GroupMembershipOperationService>());
        services.TryAddScoped<GroupManagementService>();
        services.AddGroupActorContextServices();
        services.AddGroupDiscoveryServices();
        services.AddGroupPersonalParticipationServices();
        services.AddHostedService<GroupsStartupCheck>();
        return services;
    }

    private sealed record GroupsRuntime(string Role, IReadOnlySet<string> Modules);

    private sealed class GroupsStartupCheck(IServiceScopeFactory services, GroupsRuntime runtime) : IHostedService
    {
        public async Task StartAsync(CancellationToken token)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var groups = scope.ServiceProvider.GetRequiredService<GroupsDbContext>();
                var accounts = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
                var groupSettings = new NpgsqlConnectionStringBuilder(groups.Database.GetConnectionString());
                var accountSettings = new NpgsqlConnectionStringBuilder(accounts.Database.GetConnectionString());
                if (groupSettings.Host != accountSettings.Host || groupSettings.Port != accountSettings.Port
                    || groupSettings.Database != accountSettings.Database || groupSettings.Username != accountSettings.Username
                    || (await groups.Database.GetPendingMigrationsAsync(token)).Any()
                    || (await accounts.Database.GetPendingMigrationsAsync(token)).Any())
                    throw new InvalidOperationException("Shared Groups requires current Group and identity migrations on the same database.");

                await using var transaction = await groups.Database.BeginTransactionAsync(token);
                await VerifyAuthorityAsync(groups, token);
                await groups.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role', {runtime.Role}, true)", token);
                await VerifyRuntimeAsync(groups, token);
                await transaction.RollbackAsync(token);
            }
            catch (Exception error) when (error is DbException or TimeoutException
                or InvalidOperationException { InnerException: DbException or TimeoutException })
            {
                throw new InvalidOperationException("Shared Groups database verification failed.");
            }
        }

        private async Task VerifyAuthorityAsync(GroupsDbContext groups, CancellationToken token)
        {
            bool ready = await groups.Database.SqlQuery<bool>($"""
                SELECT NOT EXISTS (
                    SELECT FROM (VALUES
                        ('andril_groups.club_limit(uuid)', 'andril_economy.premium_entitlements', {runtime.Modules.Contains("hobbies")}),
                        ('andril_groups.pair_is_blocked(uuid,uuid,text)', 'andril_social.social_profile_blocks', {runtime.Modules.Contains("social")}),
                        ('andril_groups.profile_is_restricted(uuid,text)', 'andril_social.social_profile_restrictions', {runtime.Modules.Contains("social")})
                    ) AS dependencies(signature, relation_name, required)
                    LEFT JOIN pg_proc f ON f.oid = to_regprocedure(dependencies.signature)
                    LEFT JOIN pg_class relation ON relation.oid = to_regclass(dependencies.relation_name)
                    LEFT JOIN pg_roles owner_role ON owner_role.oid = f.proowner
                    WHERE dependencies.required AND (f.oid IS NULL OR relation.oid IS NULL OR NOT f.prosecdef
                        OR NOT has_schema_privilege(f.proowner, relation.relnamespace, 'USAGE')
                        OR NOT has_table_privilege(f.proowner, relation.oid, 'SELECT')
                        OR (dependencies.relation_name = 'andril_economy.premium_entitlements' AND
                            (relation.relkind NOT IN ('r', 'p')
                             OR NOT has_table_privilege(f.proowner, relation.oid, 'UPDATE,DELETE,TRUNCATE')
                             OR EXISTS (SELECT FROM (VALUES ('account_id', 'uuid'), ('tier', 'integer'),
                                    ('is_active', 'boolean'), ('expires_at_utc', 'timestamp with time zone')) AS required(column_name, column_type)
                                WHERE NOT EXISTS (SELECT FROM pg_attribute attribute WHERE attribute.attrelid = relation.oid
                                    AND attribute.attname = required.column_name AND NOT attribute.attisdropped
                                    AND attribute.atttypid = to_regtype(required.column_type)))))
                        OR (relation.relrowsecurity AND NOT (owner_role.rolsuper OR owner_role.rolbypassrls
                            OR (NOT relation.relforcerowsecurity AND pg_has_role(f.proowner, relation.relowner, 'USAGE'))
                            OR (dependencies.relation_name LIKE 'andril_social.%'
                                AND EXISTS (SELECT FROM pg_policy p WHERE p.polrelid = relation.oid AND p.polname = 'social_safety_authority'
                                    AND p.polpermissive AND p.polcmd IN ('*','r') AND pg_get_expr(p.polqual, p.polrelid) = 'true'
                                    AND EXISTS (SELECT FROM unnest(p.polroles) role_id WHERE
                                        CASE WHEN role_id = 0 THEN true ELSE pg_has_role(f.proowner, role_id, 'USAGE') END))
                                AND NOT EXISTS (SELECT FROM pg_policy p WHERE p.polrelid = relation.oid AND NOT p.polpermissive
                                    AND p.polcmd IN ('*','r') AND EXISTS (SELECT FROM unnest(p.polroles) role_id WHERE
                                        CASE WHEN role_id = 0 THEN true ELSE pg_has_role(f.proowner, role_id, 'USAGE') END)))))
                        OR (dependencies.relation_name LIKE 'andril_social.%' AND NOT coalesce(
                            has_function_privilege(f.proowner, to_regprocedure('andril_social.social_safety_lock()'), 'EXECUTE'), false)))
                ) AS "Value"
                """).SingleAsync(token);
            if (!ready) throw new InvalidOperationException("A selected Shared Groups module lacks its canonical database authority.");
        }

        private static async Task VerifyRuntimeAsync(GroupsDbContext groups, CancellationToken token)
        {
            bool ready = await groups.Database.SqlQueryRaw<bool>("""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND has_schema_privilege('andril_groups', 'USAGE') AND NOT has_schema_privilege('andril_groups', 'CREATE')
                    AND NOT EXISTS (SELECT FROM unnest(ARRAY[
                        'andril_groups.owns_current_profile(uuid,text)', 'andril_groups.read_group_details(uuid,text,uuid)',
                        'andril_groups.create_group(uuid,text,integer,uuid,text,text,text,boolean)',
                        'andril_groups.update_group(uuid,text,uuid,text,text,text,boolean,bigint)',
                        'andril_groups.mutate_membership(uuid,text,uuid,uuid,text,bigint)', 'andril_groups.write_authority(text,uuid,text,boolean)',
                        'andril_groups.read_profile_evidence(uuid,text,uuid)', 'andril_groups.read_participation_evidence(uuid,text,uuid)',
                        'andril_groups.read_club_capacity(uuid,uuid)', 'andril_groups.read_discovery(uuid,text,integer,text,integer)',
                        'andril_groups.read_own_participation(uuid,text,uuid)', 'andril_groups.read_host_requests(uuid,text,uuid,uuid,integer)'
                    ]) AS functions(signature) WHERE NOT coalesce(has_function_privilege(to_regprocedure(signature), 'EXECUTE'), false))
                    AND NOT EXISTS (SELECT FROM unnest(ARRAY[
                        'andril_groups.groups', 'andril_groups.memberships', 'andril_groups.join_requests', 'andril_groups.capabilities',
                        'andril_groups.audit', 'andril_groups.classroom_host_grants', 'andril_groups.module_blocks',
                        'andril_groups.module_restrictions', 'andril_groups.moderator_grants', 'andril_groups.authority_audit'
                    ]) AS tables(name) LEFT JOIN pg_class relation ON relation.oid = to_regclass(tables.name)
                        WHERE relation.oid IS NULL OR NOT row_security_active(relation.oid)
                            OR pg_has_role(current_user, relation.relowner, 'MEMBER')
                            OR has_any_column_privilege(relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                            OR has_table_privilege(relation.oid, 'DELETE,TRUNCATE,TRIGGER'))
                    AND NOT EXISTS (SELECT FROM unnest(ARRAY[
                        'andril_identity.private_module_profiles', 'andril_identity.account_demographics', 'andril_identity.accounts',
                        'andril_economy.premium_entitlements', 'andril_social.social_profile_blocks', 'andril_social.social_profile_restrictions'
                    ]) AS tables(name)
                        JOIN pg_namespace schema ON schema.nspname = split_part(tables.name, '.', 1)
                        JOIN pg_class relation ON relation.relnamespace = schema.oid AND relation.relname = split_part(tables.name, '.', 2)
                        WHERE has_any_column_privilege(relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                            OR has_table_privilege(relation.oid, 'DELETE,TRUNCATE,TRIGGER'))
                ) AS "Value" FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!ready) throw new InvalidOperationException("Shared Groups database privileges are invalid.");
        }

        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
}
