using Andril.API.Authorization.Groups;
using Andril.Groups.Interfaces;
using Andril.Groups.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.API.Extensions;

/// <summary>Explicit opt-in composition; does not activate controllers or install evidence providers.</summary>
public static class GroupDiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddGroupDiscoveryServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var application = Registrations(services, typeof(GroupDiscoveryService), typeof(GroupDiscoveryService));
        var access = Registrations(services, typeof(IGroupDiscoveryAccessService), typeof(GroupDiscoveryAccessService));
        var concrete = Registrations(services, typeof(GroupDiscoveryAccessService), typeof(GroupDiscoveryAccessService));
        if (access.Length == 1 && concrete.Length == 1 &&
            access[0].ImplementationFactory != ResolveInterface && concrete[0].ImplementationFactory != ResolveConcrete)
            throw new InvalidOperationException("Group discovery access registrations must share scoped identity.");
        if (application.Length == 0)
            services.Add(ServiceDescriptor.Scoped(typeof(GroupDiscoveryService), sp =>
                new GroupDiscoveryService(sp.GetRequiredService<IGroupDiscoveryReadRepository>(), sp.GetService<TimeProvider>())));
        if (access.Length == 1 && concrete.Length == 0)
            services.Add(ServiceDescriptor.Scoped(typeof(GroupDiscoveryAccessService), ResolveConcrete));
        else if (access.Length == 0)
        {
            if (concrete.Length == 0)
                services.Add(ServiceDescriptor.Scoped(typeof(GroupDiscoveryAccessService), sp =>
                    new GroupDiscoveryAccessService(sp.GetRequiredService<IGroupActorContext>(),
                        sp.GetRequiredService<GroupDiscoveryService>(), sp.GetService<TimeProvider>())));
            services.Add(ServiceDescriptor.Scoped(typeof(IGroupDiscoveryAccessService), ResolveInterface));
        }
        return services;
    }

    private static ServiceDescriptor[] Registrations(IServiceCollection services, Type service, Type implementation)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == service && !descriptor.IsKeyedService).ToArray();
        if (descriptors.Length > 1 || descriptors.Any(descriptor => descriptor.Lifetime != ServiceLifetime.Scoped ||
            descriptor.ImplementationInstance is not null ||
            (descriptor.ImplementationType is { } type && type != implementation)))
            throw new InvalidOperationException("Group discovery requires one compatible scoped service registration.");
        return descriptors;
    }

    private static void RequireDependencies(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IGroupDiscoveryReadRepository>();
        _ = provider.GetRequiredService<IGroupActorContext>();
    }

    private static object ResolveInterface(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<GroupDiscoveryAccessService>();
    }

    private static object ResolveConcrete(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<IGroupDiscoveryAccessService>() is GroupDiscoveryAccessService service ? service :
            throw new InvalidOperationException("Group discovery access implementation is incompatible with concrete resolution.");
    }
}
