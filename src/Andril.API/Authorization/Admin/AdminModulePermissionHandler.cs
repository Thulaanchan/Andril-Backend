using Microsoft.AspNetCore.Authorization;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Evaluates module-scoped Admin permissions using trusted grants.
/// This handler does not replace target/resource-level authorization.
/// </summary>
public sealed class AdminModulePermissionHandler(
    IAdminPermissionReader permissionReader)
    : AuthorizationHandler<AdminModulePermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminModulePermissionRequirement requirement)
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
            isAnonymous ||
            !AdminModuleScope.IsValid(requirement.ModuleScope))
        {
            context.Fail();
            return;
        }

        try
        {
            var permitted = await permissionReader.HasPermissionAsync(
                subjectId.ToString("D"),
                requirement.Permission,
                requirement.ModuleScope);

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
