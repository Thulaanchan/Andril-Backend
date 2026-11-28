using System.Data.Common;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Onboarding;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Andril.API.Extensions;

public static class SocialMatchingRuntimeExtensions
{
    public static IServiceCollection AddVerifiedSocialMatchingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("SocialMatching:EnablePersistence")) return services;
        var runtimeRole = configuration["SocialMatching:RuntimeRole"];
        var connection = configuration.GetConnectionString("AndrilAccounts");
        if (string.IsNullOrWhiteSpace(runtimeRole) || string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Social matching requires an Accounts connection and a restricted runtime role.");

        NpgsqlConnectionStringBuilder settings;
        try { settings = new NpgsqlConnectionStringBuilder(connection) { IncludeErrorDetail = false, PersistSecurityInfo = false }; }
        catch (ArgumentException) { throw new InvalidOperationException("The Social matching database configuration is invalid."); }
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException("The Social matching database configuration is invalid.");

        bool distances = configuration.GetValue<bool>("SocialMatching:EnableDistanceDiscovery");
        if (distances)
        {
            var cellSize = configuration.GetValue<decimal?>("SocialMatching:Location:CellSizeDegrees");
            var maximumAge = configuration.GetValue<TimeSpan?>("SocialMatching:Location:MaximumAge");
            if (cellSize is null || maximumAge is null)
                throw new InvalidOperationException("Social distance discovery requires an explicit cell size and freshness limit.");
            services.AddSingleton(new SocialLocationSettings(cellSize.Value, maximumAge.Value));
            services.TryAddScoped<PostgresSocialLocationRepository>();
        }

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(DbContextOptions<SocialDbContext>)))
            services.AddSocialPersistence(options => options.UseNpgsql(settings.ConnectionString, provider =>
                provider.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)),
                enforceIdentityDiscriminator: true);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped(provider => new PostgresSocialQuestionnaireLifecycleRepository(
            provider.GetRequiredService<AccountsDbContext>(), provider.GetRequiredService<DbContextOptions<SocialDbContext>>(),
            provider.GetRequiredService<ICurrentUser>(), runtimeRole));
        services.TryAddScoped<PostgresSocialProfileEvidenceRepository>();
        var interestSelections = configuration.GetValue<bool>("SocialMatching:EnableInterestSelections");
        if (interestSelections) services.TryAddScoped<PostgresSocialInterestRepository>();
        var personality = configuration.GetValue<bool>("SocialMatching:EnablePersonality");
        if (personality) services.TryAddScoped<PostgresSocialPersonalityRepository>();
        services.TryAddScoped(provider => new PostgresSocialMatchingEvidenceReader(
            provider.GetRequiredService<PostgresSocialQuestionnaireLifecycleRepository>(), interestSelections, personality));
        services.TryAddScoped(provider => new PostgresSocialMatchingEvidenceProvider(provider.GetRequiredService<PostgresSocialMatchingEvidenceReader>(),
            provider.GetService<SocialLocationSettings>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<ISocialDiscoveryEvidenceProvider>(provider => provider.GetRequiredService<PostgresSocialMatchingEvidenceProvider>());
        services.TryAddScoped<ISocialRelationshipEvidenceProvider>(provider => provider.GetRequiredService<PostgresSocialMatchingEvidenceProvider>());
        services.TryAddScoped(provider => new PostgresSocialRelationshipTransactionRepository(
            provider.GetRequiredService<AccountsDbContext>(), provider.GetRequiredService<DbContextOptions<SocialDbContext>>(),
            provider.GetRequiredService<ICurrentUser>(), runtimeRole));
        services.TryAddScoped<ISocialRelationshipTransactionRepository>(provider => provider.GetRequiredService<PostgresSocialRelationshipTransactionRepository>());
        services.TryAddScoped<ISocialRelationshipListRepository>(provider => provider.GetRequiredService<PostgresSocialRelationshipTransactionRepository>());
        services.TryAddScoped<SocialRelationshipListAccessService>();
        services.TryAddScoped(provider => new PostgresSocialSafetyRepository(provider.GetRequiredService<DbContextOptions<SocialDbContext>>(),
            provider.GetRequiredService<ICurrentUser>(), provider.GetRequiredService<IOnboardingIdentity>(), runtimeRole));
        services.TryAddScoped<ISocialDiscoveryAccessService, SocialDiscoveryAccessService>();
        services.AddSocialRelationshipServices();
        services.AddSingleton(new MatchingRuntime(runtimeRole, distances, interestSelections, personality));
        services.AddHostedService<MatchingStartupCheck>();
        return services;
    }

    private sealed record MatchingRuntime(string Role, bool Distances, bool Interests, bool Personality);

    private sealed class MatchingStartupCheck(IServiceScopeFactory serviceScopes, MatchingRuntime runtime) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var serviceScope = serviceScopes.CreateAsyncScope();
                var database = serviceScope.ServiceProvider.GetRequiredService<SocialDbContext>();
                var identity = serviceScope.ServiceProvider.GetRequiredService<AccountsDbContext>();
                var social = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
                var accounts = new NpgsqlConnectionStringBuilder(identity.Database.GetConnectionString());
                if (social.Host != accounts.Host || social.Port != accounts.Port || social.Database != accounts.Database || social.Username != accounts.Username
                    || (await database.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                    throw new InvalidOperationException("Social matching requires the current migrations on the Accounts database.");
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role', {runtime.Role}, true)", cancellationToken);
                var ready = await database.Database.SqlQuery<bool>($"""
                    SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                        AND has_schema_privilege('andril_social', 'USAGE') AND NOT has_schema_privilege('andril_social', 'CREATE')
                        AND NOT EXISTS (SELECT 1 FROM unnest(ARRAY[
                            'andril_social.questionnaire_owner_visible(uuid)', 'andril_social.social_reciprocal_interest(uuid,uuid)',
                            'andril_social.try_form_social_match(uuid,uuid,uuid,timestamp with time zone)', 'andril_social.social_safety_lock()',
                            'andril_social.social_safety_decision(uuid,uuid)', 'andril_social.set_social_block(uuid,boolean)',
                            'andril_social.set_social_restriction(uuid,boolean)', 'andril_social.read_social_matching_evidence(uuid,uuid,integer)'
                        ]) AS required(signature) WHERE NOT has_function_privilege(required.signature, 'EXECUTE'))
                        AND NOT EXISTS (SELECT 1 FROM unnest(ARRAY[
                            'andril_social.social_directional_interests', 'andril_social.social_matches', 'andril_social.social_matching_preferences',
                            'andril_social.social_location_evidence', 'andril_social.social_profile_blocks', 'andril_social.social_profile_restrictions',
                            'andril_social.social_safety_audit', 'andril_social.social_moderation_grants'
                        ]) AS entry(name) JOIN pg_class relation ON relation.oid = entry.name::regclass
                            WHERE NOT row_security_active(relation.oid) OR pg_has_role(current_user, relation.relowner, 'MEMBER'))
                        AND NOT EXISTS (SELECT 1 FROM unnest(ARRAY[
                            'andril_social.social_profile_blocks', 'andril_social.social_profile_restrictions', 'andril_social.social_safety_audit',
                            'andril_social.social_moderation_grants', 'andril_identity.private_module_profiles', 'andril_identity.account_demographics'
                        ]) AS entry(name)
                            JOIN pg_namespace schema ON schema.nspname = split_part(entry.name, '.', 1)
                            JOIN pg_class relation ON relation.relnamespace = schema.oid AND relation.relname = split_part(entry.name, '.', 2)
                            WHERE has_any_column_privilege(relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                                OR has_table_privilege(relation.oid, 'DELETE,TRUNCATE'))
                        AND (NOT {runtime.Interests} OR (
                            NOT EXISTS (SELECT FROM unnest(ARRAY['andril_social.read_social_interest_catalogue(uuid)',
                                'andril_social.read_own_social_interests(uuid)', 'andril_social.replace_own_social_interests(uuid,uuid[])',
                                'andril_social.read_social_matching_interests(uuid,uuid,integer)']) signature
                                WHERE NOT coalesce(has_function_privilege(to_regprocedure(signature),'EXECUTE'), false))
                            AND NOT coalesce(has_function_privilege(to_regprocedure('andril_social.provision_social_interest(uuid,text,boolean)'), 'EXECUTE'), false)
                            AND NOT EXISTS (SELECT FROM unnest(ARRAY['social_interest_catalogue',
                                'social_interest_selection_state', 'social_interest_selections']) entry(table_name)
                                LEFT JOIN pg_class r ON r.relnamespace = to_regnamespace('andril_social') AND r.relname = entry.table_name
                                WHERE r.oid IS NULL OR NOT row_security_active(r.oid) OR pg_has_role(current_user, r.relowner, 'MEMBER')
                                    OR has_any_column_privilege(r.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                                    OR has_table_privilege(r.oid, 'DELETE,TRUNCATE'))))
                        AND (NOT {runtime.Personality} OR (
                            NOT EXISTS (SELECT FROM unnest(ARRAY['andril_social.read_social_matching_personality(uuid,uuid,integer)',
                                'andril_social.set_social_archetype_confirmation(uuid,uuid,integer,integer,integer)',
                                'andril_social.set_social_personality_preferences(uuid,text,text,text)']) signature
                                WHERE NOT coalesce(has_function_privilege(to_regprocedure(signature),'EXECUTE'), false))
                            AND EXISTS (SELECT FROM pg_class r WHERE r.oid = to_regclass('andril_social.social_archetype_confirmations')
                                AND row_security_active(r.oid) AND NOT pg_has_role(current_user, r.relowner, 'MEMBER')
                                AND NOT has_any_column_privilege(r.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                                AND NOT has_table_privilege(r.oid, 'DELETE,TRUNCATE'))))
                        AND has_table_privilege('andril_social.social_directional_interests', 'SELECT')
                        AND has_table_privilege('andril_social.social_directional_interests', 'INSERT')
                        AND has_column_privilege('andril_social.social_directional_interests', 'is_positive', 'UPDATE')
                        AND has_column_privilege('andril_social.social_directional_interests', 'recorded_at_utc', 'UPDATE')
                        AND NOT has_table_privilege('andril_social.social_directional_interests', 'DELETE,TRUNCATE')
                        AND NOT has_column_privilege('andril_social.social_directional_interests', 'actor_profile_id', 'UPDATE')
                        AND NOT has_column_privilege('andril_social.social_directional_interests', 'target_profile_id', 'UPDATE')
                        AND has_table_privilege('andril_social.social_matches', 'SELECT')
                        AND has_column_privilege('andril_social.social_matches', 'state', 'UPDATE')
                        AND has_column_privilege('andril_social.social_matches', 'revoked_at_utc', 'UPDATE')
                        AND NOT has_table_privilege('andril_social.social_matches', 'INSERT,DELETE,TRUNCATE')
                        AND NOT has_column_privilege('andril_social.social_matches', 'first_profile_id', 'UPDATE')
                        AND NOT has_column_privilege('andril_social.social_matches', 'second_profile_id', 'UPDATE')
                        AND has_table_privilege('andril_social.social_matching_preferences', 'SELECT')
                        AND has_table_privilege('andril_social.social_matching_preferences', 'INSERT')
                        AND has_column_privilege('andril_social.social_matching_preferences', 'revision', 'UPDATE')
                        AND NOT has_column_privilege('andril_social.social_matching_preferences', 'profile_id', 'UPDATE')
                        AND NOT has_table_privilege('andril_social.social_matching_preferences', 'DELETE,TRUNCATE')
                        AND (NOT {runtime.Distances} OR (has_table_privilege('andril_social.social_location_evidence', 'SELECT')
                            AND has_table_privilege('andril_social.social_location_evidence', 'INSERT')
                            AND has_column_privilege('andril_social.social_location_evidence', 'revision', 'UPDATE')
                            AND NOT has_column_privilege('andril_social.social_location_evidence', 'profile_id', 'UPDATE')
                            AND NOT has_table_privilege('andril_social.social_location_evidence', 'DELETE,TRUNCATE')))) AS "Value"
                    FROM pg_roles WHERE rolname = current_user
                    """).SingleAsync(cancellationToken);
                if (!ready) throw new InvalidOperationException("Social matching database privileges are invalid.");
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException { InnerException: DbException or TimeoutException })
            {
                throw new InvalidOperationException("Social matching database verification failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
