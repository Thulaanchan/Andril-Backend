using System;
using Andril.API.Authorization.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Service collection extensions for configuring opt-in Social discovery authorization services (SOC-02).
/// Registers <see cref="ISocialDiscoveryAccessService"/> without activating production persistence or controllers.
/// </summary>
public static class SocialDiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers opt-in caller-scoped Social discovery access services.
    /// Requires an authoritative <see cref="ISocialDiscoveryEvidenceProvider"/> and <see cref="Andril.Common.Accounts.Profiles.IPrivateProfileService"/>
    /// to be registered separately by the host or test harness.
    /// Preserves existing registrations and prevents duplicate descriptors.
    /// Does not activate production persistence, background tasks, or discovery controllers.
    /// </summary>
    public static IServiceCollection AddSocialDiscoveryServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISocialDiscoveryAccessService, SocialDiscoveryAccessService>();
        services.TryAddScoped<SocialDiscoveryAccessService>();

        return services;
    }
}
