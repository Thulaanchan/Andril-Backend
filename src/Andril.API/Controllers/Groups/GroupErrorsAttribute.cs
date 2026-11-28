using Andril.API.Authorization.Groups;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.Repositories.Groups;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Andril.API.Controllers.Groups;

[AttributeUsage(AttributeTargets.Class)]
public sealed class GroupErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            _ when GroupAvailability.IsUnavailable(context.Exception) => 503,
            AccountAuthenticationRequiredException => 401,
            ProfileAccessDeniedException or ProfileMissingException or StarterModuleAccountMissingException or UnauthorizedAccessException => 403,
            GroupQuotaReachedException => 409,
            ArgumentException => 400,
            _ => 0
        };
        if (status == 0) return;
        context.Result = Problem(status, status == 409 ? "creation_quota" : null);
        context.ExceptionHandled = true;
    }

    internal static ObjectResult Problem(int status, string? code = null)
    {
        var title = status switch
        {
            400 => "Invalid Group request.",
            401 => "Authentication is required.",
            403 => "Group access denied.",
            409 => "The Group operation conflicts with current state.",
            503 => "Group authority is unavailable.",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        var detail = new ProblemDetails { Status = status, Title = title };
        if (code is not null) detail.Extensions.Add("code", code);
        return new ObjectResult(detail) { StatusCode = status };
    }
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class SafeGroupModelStateAttribute : ActionFilterAttribute
{
    public SafeGroupModelStateAttribute() => Order = int.MinValue;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) context.Result = GroupErrorsAttribute.Problem(400);
    }
}
