using System.Data.Common;
using System.Text.Json;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.Repositories.Social;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Andril.API.Controllers.Social;

[AttributeUsage(AttributeTargets.Class)]
public sealed class SocialMatchingErrorsAttribute : ExceptionFilterAttribute
{
    internal static bool IsDatabaseUnavailable(Exception error) => error is AccountStoreUnavailableException
        or DbException or DbUpdateException or TimeoutException
        or InvalidOperationException { InnerException: DbException or TimeoutException };

    public override void OnException(ExceptionContext context)
    {
        var error = context.Exception;
        var (status, title) = error switch
        {
            _ when IsDatabaseUnavailable(error) => (503, "Social matching storage is unavailable."),
            ProfileAccessDeniedException or ProfileMissingException => (403, "Social profile access denied."),
            SocialPersonalityConflictException => (409, "Current Social personality evidence has changed."),
            ArgumentException => (400, "Invalid Social request."),
            _ => (0, "")
        };
        if (status == 0) return;
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class StrictJsonBodyAttribute : Attribute, IAsyncResourceFilter
{
    protected virtual string InvalidBodyTitle => "Invalid request body.";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!request.HasJsonContentType())
        {
            context.Result = new UnsupportedMediaTypeResult();
            return;
        }

        const int maximumBytes = 8192;
        request.EnableBuffering(bufferThreshold: maximumBytes, bufferLimit: maximumBytes);
        try
        {
            if (request.ContentLength is > maximumBytes) throw new JsonException();
            using var document = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false }, context.HttpContext.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(properties => properties.Count() > 1))
                throw new JsonException();
            request.Body.Position = 0;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails { Status = 400, Title = InvalidBodyTitle });
            return;
        }
        await next();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class StrictSocialJsonAttribute : StrictJsonBodyAttribute
{
    protected override string InvalidBodyTitle => "Invalid Social request body.";
}
