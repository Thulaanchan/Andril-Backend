using Andril.API.Authorization.Groups;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.API.Extensions;

/// <summary>Explicitly opt-in trusted Group actor integration; never activates HTTP endpoints.</summary>
public static class GroupActorContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers one scoped actor under both service types. Authoritative dependencies must
    /// be supplied separately; no provider, profile service or permissive fallback is installed.
    /// Existing scoped concrete registrations or compatible factories are preserved.
    /// </summary>
    public static IServiceCollection AddGroupActorContextServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var actors = services.Where(descriptor => !descriptor.IsKeyedService &&
            descriptor.ServiceType == typeof(IGroupActorContext)).ToArray();
        var concretes = services.Where(descriptor => !descriptor.IsKeyedService &&
            descriptor.ServiceType == typeof(GroupActorContext)).ToArray();

        Validate(actors);
        Validate(concretes);
        if (actors.Length == 1 && concretes.Length == 1)
        {
            // Two independent descriptors could resolve different actors within one request.
            if (actors[0].ImplementationFactory != ResolveInterface &&
                concretes[0].ImplementationFactory != ResolveConcrete)
                throw new InvalidOperationException("Group actor registrations must share one scoped instance.");
            return services;
        }

        if (actors.Length == 1)
            services.Add(ServiceDescriptor.Scoped(typeof(GroupActorContext), ResolveConcrete));
        else
        {
            if (concretes.Length == 0)
                services.Add(ServiceDescriptor.Scoped(typeof(GroupActorContext), CreateActor));
            services.Add(ServiceDescriptor.Scoped(typeof(IGroupActorContext), ResolveInterface));
        }

        return services;
    }

    private static void Validate(ServiceDescriptor[] descriptors)
    {
        if (descriptors.Length > 1 || descriptors.Any(descriptor =>
                descriptor.Lifetime != ServiceLifetime.Scoped ||
                descriptor.ImplementationInstance is not null ||
                (descriptor.ImplementationType is { } type && type != typeof(GroupActorContext))))
            throw new InvalidOperationException("Group actor integration requires a single compatible scoped registration.");
    }

    private static void RequireDependencies(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IPrivateProfileService>();
        _ = provider.GetRequiredService<IGroupAuthoritativeEvidenceProvider>();
    }

    private static object CreateActor(IServiceProvider provider) => new GroupActorContext(
        provider.GetRequiredService<IPrivateProfileService>(),
        provider.GetRequiredService<IGroupAuthoritativeEvidenceProvider>(),
        provider.GetService<TimeProvider>() ?? TimeProvider.System);

    private static object ResolveInterface(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<GroupActorContext>();
    }

    private static object ResolveConcrete(IServiceProvider provider)
    {
        RequireDependencies(provider);
        return provider.GetRequiredService<IGroupActorContext>() is GroupActorContext actor
            ? actor
            : throw new InvalidOperationException("The registered Group actor is incompatible with trusted concrete resolution.");
    }
}
