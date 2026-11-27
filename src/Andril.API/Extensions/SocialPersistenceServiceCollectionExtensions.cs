using System;
using System.Linq;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Interfaces;
using Andril.Social.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Andril.API.Extensions;

/// <summary>
/// Production service registration extensions for the isolated Social relational database persistence layer.
/// Configures <see cref="SocialDbContext"/>, <see cref="ISocialQuestionnaireReadAdapter"/>,
/// <see cref="IQuestionnairePersistenceRepository"/>, and <see cref="SocialQuestionnaireResponseScopedLookupService"/>.
/// Enforces fail-closed configuration validation and isolated migration history verification without opening network connections.
/// </summary>
public static class SocialPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddSocialPersistence(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase,
        bool enforceIdentityDiscriminator = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDatabase);

        // Register the dedicated Social database context.
        // Validate options when resolved, without opening a network connection.
        services.AddDbContext<SocialDbContext>(options =>
        {
            configureDatabase(options);
            var relational = options.Options.Extensions
                .OfType<RelationalOptionsExtension>().SingleOrDefault();
            if (relational?.MigrationsHistoryTableName != SocialDbContextFactory.MigrationsHistoryTableName
                || relational.MigrationsHistoryTableSchema != SocialDbContext.Schema)
            {
                throw new InvalidOperationException(
                    $"Social requires migration history {SocialDbContext.Schema}.{SocialDbContextFactory.MigrationsHistoryTableName}.");
            }
        });

        // Register the owner-scoped PostgreSQL read adapter
        services.AddScoped<ISocialQuestionnaireReadAdapter>(sp =>
        {
            var db = sp.GetRequiredService<SocialDbContext>();
            return new PostgresSocialQuestionnaireReadAdapter(db, enforceIdentityDiscriminator);
        });

        // Register the concrete persistence repository
        services.AddScoped<IQuestionnairePersistenceRepository>(sp =>
        {
            var db = sp.GetRequiredService<SocialDbContext>();
            var readAdapter = sp.GetRequiredService<ISocialQuestionnaireReadAdapter>();
            return new SocialQuestionnairePersistenceRepository(db, readAdapter, enforceIdentityDiscriminator);
        });

        // Register the scoped response lookup service
        services.AddScoped<SocialQuestionnaireResponseScopedLookupService>();

        return services;
    }

    public static IServiceCollection AddVerifiedSocialQuestionnairePersistence(this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = configuration.GetConnectionString("AndrilAccounts");
        var runtimeRole = configuration["SocialQuestionnaire:RuntimeRole"];
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(runtimeRole))
            throw new InvalidOperationException("Social questionnaire persistence requires an Accounts connection and a runtime role.");

        NpgsqlConnectionStringBuilder settings;
        try { settings = new NpgsqlConnectionStringBuilder(connection) { IncludeErrorDetail = false, PersistSecurityInfo = false }; }
        catch (ArgumentException) { throw new InvalidOperationException("The Social questionnaire database configuration is invalid."); }
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException("The Social questionnaire database configuration is invalid.");

        services.AddSocialPersistence(options => options.UseNpgsql(settings.ConnectionString, provider =>
            provider.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)),
            enforceIdentityDiscriminator: true);
        services.TryAddScoped<ISocialQuestionnaireLifecycleRepository>(provider => new PostgresSocialQuestionnaireLifecycleRepository(
            provider.GetRequiredService<AccountsDbContext>(), provider.GetRequiredService<DbContextOptions<SocialDbContext>>(),
            provider.GetRequiredService<ICurrentUser>(), runtimeRole));
        services.AddSingleton(new SocialRuntimeConfiguration(runtimeRole));
        services.AddHostedService<SocialPersistenceStartupCheck>();
        return services.AddSocialQuestionnaireResponseServices();
    }

    private sealed record SocialRuntimeConfiguration(string RuntimeRole);

    private sealed class SocialPersistenceStartupCheck(IServiceScopeFactory scopes, SocialRuntimeConfiguration runtime) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<SocialDbContext>();
                var identity = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
                // A shared Accounts transaction is required at runtime, so verify the same connection here.
                var socialSettings = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
                var identitySettings = new NpgsqlConnectionStringBuilder(identity.Database.GetConnectionString());
                if (socialSettings.Host != identitySettings.Host || socialSettings.Port != identitySettings.Port ||
                    socialSettings.Database != identitySettings.Database || socialSettings.Username != identitySettings.Username)
                    throw new InvalidOperationException("The Accounts and Social database connections must match.");

                if ((await database.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                    throw new InvalidOperationException("The Social questionnaire migrations must be applied before activation.");

                await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role', {runtime.RuntimeRole}, true)", cancellationToken);
                var access = await database.Database.SqlQueryRaw<bool>("""
                    SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                        AND has_schema_privilege('andril_social','USAGE')
                        AND has_function_privilege('andril_social.questionnaire_owner_visible(uuid)','EXECUTE')
                        AND EXISTS (SELECT 1 FROM pg_constraint
                            WHERE conname = 'fk_social_questionnaire_current_social_profile' AND convalidated
                            AND conrelid = 'andril_social.social_questionnaire_current_responses'::regclass)
                        AND NOT EXISTS (SELECT 1 FROM unnest(ARRAY['andril_social.social_questionnaire_responses',
                            'andril_social.social_questionnaire_answers','andril_social.social_questionnaire_current_responses']) AS entry(name)
                            WHERE NOT row_security_active(entry.name::regclass) OR NOT has_table_privilege(entry.name,'SELECT')
                                OR NOT has_table_privilege(entry.name,'INSERT')
                                OR has_table_privilege(entry.name,'UPDATE,DELETE,TRUNCATE'))
                        AND NOT has_table_privilege((SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                            WHERE n.nspname = 'andril_identity' AND c.relname = 'private_module_profiles'), 'SELECT')) AS "Value"
                    FROM pg_roles WHERE rolname = current_user
                    """).SingleAsync(cancellationToken);
                if (!access) throw new InvalidOperationException("Social questionnaire database privileges are invalid.");
                await tx.RollbackAsync(cancellationToken);
            }
            catch (Exception error) when (error is DbException or TimeoutException)
            {
                throw new InvalidOperationException("Social questionnaire database verification failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
