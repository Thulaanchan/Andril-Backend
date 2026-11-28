using System.Reflection;
using Andril.API.Controllers.Social;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Andril.API.Extensions;

public static class SocialQuestionnaireEndpointExtensions
{
    public static IMvcBuilder AddControlledSocialQuestionnaireResponses(this IMvcBuilder mvc, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(mvc);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>("SocialQuestionnaire:EnablePersistence")) return mvc;

        mvc.Services.AddVerifiedSocialQuestionnairePersistence(configuration);
        mvc.PartManager.FeatureProviders.Add(new QuestionnaireResponseControllerFeature());
        return mvc;
    }

    private sealed class QuestionnaireResponseControllerFeature : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var responseController = typeof(SocialQuestionnaireResponsesController).GetTypeInfo();
            if (!feature.Controllers.Contains(responseController)) feature.Controllers.Add(responseController);
        }
    }
}
