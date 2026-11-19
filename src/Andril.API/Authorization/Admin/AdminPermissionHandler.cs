using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Checks account-scoped Admin permissions using trusted grants.
/// Not suitable for module/resource-scoped actions without additional checks.
/// </summary>
public sealed class AdminPermissionHandler(
    IAdminPermissionReader permissionReader)
    : AuthorizationHandler<AdminPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminPermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Fail();
            return;
        }

        var subjects = context.User.FindAll("sub").ToArray();
        var roles = context.User.FindAll("role").ToArray();
        var anonymousClaims = context.User.FindAll("is_anonymous").ToArray();

        if (subjects.Length != 1 ||
            !Guid.TryParse(subjects[0].Value, out var subjectId) ||
            subjectId == Guid.Empty ||
            roles.Length != 1 ||
            roles[0].Value != "authenticated" ||
            anonymousClaims.Length != 1 ||
            !bool.TryParse(anonymousClaims[0].Value, out var isAnonymous) ||
            isAnonymous)
        {
            context.Fail();
            return;
        }

        try
        {
            var permitted = await permissionReader.HasPermissionAsync(
                subjectId.ToString("D"),
                requirement.Permission,
                moduleScope: null);

            if (permitted)
                context.Succeed(requirement);
            else
                context.Fail();
        }
        catch (Exception)
        {
            context.Fail();
        }
    }
}
