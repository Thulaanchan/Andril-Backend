using Andril.API.Authorization.Social;
using Andril.Social.Personality;
using Andril.Social.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.API.Extensions;

public static class SocialQuestionnaireServiceCollectionExtensions
{
    public static IServiceCollection AddSocialQuestionnaireServices(this IServiceCollection services)
    {
        services.AddSingleton(new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]));
        services.AddScoped<QuestionnaireApplicationService>();
        services.AddScoped<ISocialQuestionnaireDefinitionAccessService, SocialQuestionnaireDefinitionAccessService>();

        return services;
    }
}
