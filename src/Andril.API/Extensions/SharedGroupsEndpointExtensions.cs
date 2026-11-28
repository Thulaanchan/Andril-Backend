using System.Reflection;
using Andril.API.Controllers.Groups;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Andril.API.Extensions;

public static class SharedGroupsEndpointExtensions
{
    public static IMvcBuilder AddControlledSharedGroups(this IMvcBuilder mvc, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("SharedGroups:EnablePersistence")) return mvc;
        mvc.PartManager.FeatureProviders.Add(new GroupsControllers());
        return mvc;
    }

    private sealed class GroupsControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in new[] { typeof(GroupManagementController), typeof(GroupMembershipController) })
            {
                var info = controller.GetTypeInfo();
                if (!feature.Controllers.Contains(info)) feature.Controllers.Add(info);
            }
        }
    }
}
