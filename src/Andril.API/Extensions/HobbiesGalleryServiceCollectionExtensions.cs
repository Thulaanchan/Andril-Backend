
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Configurations;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.API.Extensions;

public static class HobbiesGalleryServiceCollectionExtensions
{
    public static IServiceCollection AddHobbiesGallery<TActorContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase)
        where TActorContext : class, IGalleryActorContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDatabase);

        // Register the Gallery database context.
        // Validate when scoped options are resolved, without opening a connection.
        // The caller retains ownership of the connection and provider settings.
        services.AddDbContext<HobbiesGalleryDbContext>(
            options =>
            {
                configureDatabase(options);
                var relational = options.Options.Extensions
                    .OfType<RelationalOptionsExtension>().SingleOrDefault();
                if (relational?.MigrationsHistoryTableName != "__EFMigrationsHistory_Gallery"
                    || relational.MigrationsHistoryTableSchema != HobbiesDatabaseOptions.Schema)
                {
                    throw new InvalidOperationException(
                        "Gallery requires migration history andril_hobbies.__EFMigrationsHistory_Gallery.");
                }
            });

        // Register the authenticated module actor context.
        // The implementation must obtain identity from trusted
        // server-side authentication, never client-supplied IDs.
        services.AddScoped<
            IGalleryActorContext,
            TActorContext>();

        // Register the owner-scoped Gallery repository.
        services.AddScoped<IGalleryRepository>(
            serviceProvider =>
            {
                var dbContext = serviceProvider
                    .GetRequiredService<HobbiesGalleryDbContext>();

                return new GalleryRepository<
                    HobbiesGalleryDbContext>(dbContext);
            });

        // Register Gallery business logic.
        services.AddScoped<
            IGalleryService,
            GalleryService>();

        return services;
    }
}
