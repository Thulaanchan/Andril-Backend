using System;
using Andril.API.Authorization.Social;
using Andril.Social.Personality;
using Andril.Social.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Service collection extensions for configuring Social questionnaire definition, validation,
/// and response persistence orchestration services.
/// Preserves production startup behavior while providing an explicitly opt-in registration path
/// for persistent questionnaire response coordination without premature database activation.
/// </summary>
public static class SocialQuestionnaireServiceCollectionExtensions
{
    /// <summary>
    /// Registers baseline Social questionnaire definition catalog, application, and validation services.
    /// Preserves existing production host behavior with zero unresolved dependencies and without activating persistence.
    /// </summary>
    public static IServiceCollection AddSocialQuestionnaireServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]));
        services.TryAddScoped<QuestionnaireApplicationService>();
        services.TryAddScoped<ISocialQuestionnaireDefinitionAccessService, SocialQuestionnaireDefinitionAccessService>();
        services.TryAddScoped<ISocialQuestionnaireSubmissionValidationService, SocialQuestionnaireSubmissionValidationService>();
        services.TryAddScoped<QuestionnaireAccessService>();

        return services;
    }

    /// <summary>
    /// Explicitly opt-in service registration for authorized Social questionnaire response persistence orchestration
    /// and response read access services. Requires persistence dependencies (such as <see cref="Andril.Social.Interfaces.IQuestionnairePersistenceRepository"/>)
    /// to be registered separately by the host or test harness.
    /// Fails closed if required persistence dependencies are missing upon resolution.
    /// Does not initiate database connections or prematurely activate production persistence.
    /// </summary>
    public static IServiceCollection AddSocialQuestionnaireResponseServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ensure baseline questionnaire catalog and validation services are present
        services.AddSocialQuestionnaireServices();

        // Register scoped response lookup service and orchestrators
        services.TryAddScoped<SocialQuestionnaireResponseScopedLookupService>();
        services.TryAddScoped<ISocialQuestionnairePersistentSubmissionOrchestrator, SocialQuestionnairePersistentSubmissionOrchestrator>();
        services.TryAddScoped<ISocialQuestionnaireResponseReadAccessService, SocialQuestionnaireResponseReadAccessService>();
        services.TryAddScoped<ISocialQuestionnaireLifecycleAccessService, SocialQuestionnaireLifecycleAccessService>();

        return services;
    }
}
