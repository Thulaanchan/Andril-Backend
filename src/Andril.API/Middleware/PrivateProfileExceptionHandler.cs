using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class PrivateProfileExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
                when HttpMethods.IsPut(context.Request.Method)
                    && context.Request.Path.StartsWithSegments("/api/v1/account/profiles", StringComparison.OrdinalIgnoreCase)
                => (413, "Private profile request too large"),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
                when HttpMethods.IsPost(context.Request.Method)
                    && context.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ControllerTypeInfo.AsType()
                        == typeof(Controllers.Common.ProfilePhotosController)
                => (413, "Private photo upload too large"),
            PhotoUploadTooLargeException => (413, "Private photo upload too large"),
            ProfileValidationException => (400, "Invalid private profile request"),
            ProfileNameConflictException => (409, "Display name unavailable"),
            PhotoDependencyException => (503, "Private photo validation unavailable"),
            PhotoUploadInvalidException => (400, "Invalid private photo upload"),
            PhotoUploadConflictException => (409, "Photo request conflicts with an existing upload"),
            ProfileMissingException => (404, "Private profile not found"),
            ProfileAccessDeniedException => (403, "Private profile access denied"),
            ProfileContractPendingException => (503, "Approved profile contract unavailable"),
            _ => (0, "")
        };
        if (status == 0) return false;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        { HttpContext = context, ProblemDetails = new() { Status = status, Title = title } });
        return true;
    }
}
