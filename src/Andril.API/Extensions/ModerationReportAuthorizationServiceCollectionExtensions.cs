using Andril.Common.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Registers moderation report access verification services.
///
/// Defaults to denying all report submissions when no
/// trusted access verifier has been registered.
///
/// This method does not enable moderation endpoints.
/// </summary>
public static class ModerationReportAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddModerationReportAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<
            IModerationReportAccessVerifier,
            DenyAllModerationReportAccessVerifier>();

        return services;
    }
}
