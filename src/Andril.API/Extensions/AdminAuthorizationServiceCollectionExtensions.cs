using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Registers the fail-closed Admin authorization foundation.
/// Does not enable Admin endpoints or provision staff grants.
/// </summary>
public static class AdminAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddDenyAllAdminAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization();

        services.AddScoped<IAdminPermissionReader,
            DenyAllAdminPermissionReader>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IAuthorizationHandler,
                AdminPermissionHandler>());

        return services;
    }
}
