using Andril.API.Authorization.Groups;
using Andril.Groups.Interfaces;
using Andril.Groups.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.API.Extensions;

/// <summary>Opt-in composition for authorized personal Group participation reads.</summary>
public static class GroupPersonalParticipationServiceCollectionExtensions
{
    /// <summary>
    /// Registers scoped services. The host supplies the authoritative repository and trusted actor context.
    /// </summary>
    public static IServiceCollection AddGroupPersonalParticipationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var application = Registration(services, typeof(GroupPersonalParticipationService), typeof(GroupPersonalParticipationService));
        var access = Registration(services, typeof(IGroupPersonalParticipationAccessService), typeof(GroupPersonalParticipationAccessService));
        var concrete = Registration(services, typeof(GroupPersonalParticipationAccessService), typeof(GroupPersonalParticipationAccessService));

        if (access is not null && concrete is not null && access.ImplementationFactory != ResolveInterface &&
            concrete.ImplementationFactory != ResolveConcrete)
            throw new InvalidOperationException("Personal Group participation access registrations must share one scoped instance.");

        if (application is null)
            services.AddScoped(sp => new GroupPersonalParticipationService(
                sp.GetRequiredService<IGroupPersonalParticipationReadRepository>(), sp.GetService<TimeProvider>()));

        if (access is not null && concrete is null)
            services.Add(ServiceDescriptor.Scoped(typeof(GroupPersonalParticipationAccessService), ResolveConcrete));
        else if (access is null)
        {
            if (concrete is null)
                services.Add(ServiceDescriptor.Scoped(typeof(GroupPersonalParticipationAccessService), CreateAccess));
            services.Add(ServiceDescriptor.Scoped(typeof(IGroupPersonalParticipationAccessService), ResolveInterface));
        }

        return services;
    }

    private static ServiceDescriptor? Registration(IServiceCollection services, Type serviceType, Type implementationType)
    {
        var registrations = services.Where(descriptor => descriptor.ServiceType == serviceType && !descriptor.IsKeyedService).ToArray();
        if (registrations.Length == 0) return null;

        var registration = registrations[0];
        if (registrations.Length != 1 || registration.Lifetime != ServiceLifetime.Scoped ||
            (registration.ImplementationType is { } type && type != implementationType))
            throw new InvalidOperationException("Personal Group participation requires one compatible scoped registration per service.");

        return registration;
    }

    private static object CreateAccess(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IGroupPersonalParticipationReadRepository>();
        return new GroupPersonalParticipationAccessService(provider.GetRequiredService<IGroupActorContext>(),
            provider.GetRequiredService<GroupPersonalParticipationService>(), provider.GetService<TimeProvider>());
    }

    private static void RequireDependencies(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IGroupPersonalParticipationReadRepository>();
        _ = provider.GetRequiredService<IGroupActorContext>();
    }

    private static object ResolveInterface(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<GroupPersonalParticipationAccessService>();
    }

    private static object ResolveConcrete(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<IGroupPersonalParticipationAccessService>() is GroupPersonalParticipationAccessService access
            ? access
            : throw new InvalidOperationException("The registered participation access service is incompatible with concrete resolution.");
    }
}
