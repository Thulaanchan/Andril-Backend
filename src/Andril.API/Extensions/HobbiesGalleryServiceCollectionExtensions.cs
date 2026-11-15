
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
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
        // The caller must configure the approved database provider.
        services.AddDbContext<HobbiesGalleryDbContext>(
            configureDatabase);

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
