using System;
using System.Linq;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Interfaces;
using Andril.Social.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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
}
