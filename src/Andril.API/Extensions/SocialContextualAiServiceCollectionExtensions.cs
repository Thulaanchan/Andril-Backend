using System;
using Andril.AI.Interfaces;
using Andril.AI.Recommendations;
using Andril.AI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Service collection extensions for configuring opt-in Social contextual AI consent
/// and recommendation coordination services (CF-18).
/// Registers scoped contextual AI services without activating production AI generation, external providers, or credentials.
/// </summary>
public static class SocialContextualAiServiceCollectionExtensions
{
    /// <summary>
    /// Registers opt-in caller-scoped Social contextual AI services.
    /// Registers <see cref="AiPrivacyNoticeGuard"/>, <see cref="AiContextualConsentService"/>,
    /// and <see cref="AiContextualRecommendationService"/> with Scoped lifetime.
    /// Requires trusted <see cref="IAiActorAuthorization"/>, privacy notice gate <see cref="IAiPrivacyNoticeGate"/>,
    /// authoritative consent repository <see cref="IAiContextualConsentRepository"/>,
    /// and approved recommendation generator <see cref="IAiContextualRecommendationGenerator"/>
    /// to be registered separately by the host or test harness.
    /// Preserves existing registrations and prevents duplicate descriptors.
    /// Does not activate external AI providers, billing, background jobs, or controllers.
    /// </summary>
    public static IServiceCollection AddSocialContextualAiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<AiPrivacyNoticeGuard>();
        services.TryAddScoped<AiContextualConsentService>();
        services.TryAddScoped<AiContextualRecommendationService>();

        return services;
    }
}
