using System.Reflection;
using Andril.API.Controllers.Social;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Andril.API.Extensions;

public static class SocialMatchingEndpointExtensions
{
    public static IMvcBuilder AddControlledSocialMatching(this IMvcBuilder mvc, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("SocialMatching:EnablePersistence")) return mvc;
        mvc.Services.AddVerifiedSocialMatchingPersistence(configuration);
        mvc.PartManager.FeatureProviders.Add(new MatchingControllers(configuration.GetValue<bool>("SocialMatching:EnableDistanceDiscovery"),
            configuration.GetValue<bool>("SocialMatching:EnableInterestSelections")));
        return mvc;
    }

    private sealed class MatchingControllers(bool distanceDiscovery, bool interestSelections) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            Add(typeof(SocialRelationshipsController));
            Add(typeof(SocialMatchingSettingsController));
            if (interestSelections) Add(typeof(SocialInterestSelectionsController));
            if (distanceDiscovery)
            {
                Add(typeof(SocialExploreController));
                Add(typeof(SocialLocationController));
            }

            void Add(Type controller)
            {
                var type = controller.GetTypeInfo();
                if (!feature.Controllers.Contains(type)) feature.Controllers.Add(type);
            }
        }
    }
}
