using System;
using Andril.API.Authorization.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Service collection extensions for configuring opt-in Social relationship authorization and transaction services (CF-20).
/// Registers <see cref="ISocialRelationshipAccessService"/> without activating production persistence, communication, or controllers.
/// </summary>
public static class SocialRelationshipServiceCollectionExtensions
{
    /// <summary>
    /// Registers opt-in caller-scoped Social relationship access services.
    /// Requires an authoritative <see cref="ISocialRelationshipEvidenceProvider"/>,
    /// atomic <see cref="Andril.Social.Interfaces.ISocialRelationshipTransactionRepository"/>,
    /// and <see cref="Andril.Common.Accounts.Profiles.IPrivateProfileService"/>
    /// to be registered separately by the host or test harness.
    /// Preserves existing registrations and prevents duplicate descriptors.
    /// Does not activate production persistence, background tasks, communication delivery, or relationship controllers.
    /// </summary>
    public static IServiceCollection AddSocialRelationshipServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISocialRelationshipAccessService, SocialRelationshipAccessService>();
        services.TryAddScoped<SocialRelationshipAccessService>();

        return services;
    }
}
