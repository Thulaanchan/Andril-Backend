using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Registers module-scoped Admin authorization services.
/// Uses deny-all permission lookup when no trusted reader
/// has been registered.
/// Does not enable Admin endpoints or create staff grants.
/// </summary>
public static class AdminModuleAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddAdminModuleAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization();

        services.TryAddScoped<
            IAdminPermissionReader,
            DenyAllAdminPermissionReader>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IAuthorizationHandler,
                AdminModulePermissionHandler>());

        return services;
    }
}
