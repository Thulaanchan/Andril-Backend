using Andril.Common.Accounts.Privacy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class PrivacyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (PrivacyValidationException or PrivacyNotFoundException))
            return false;

        var isValidation = exception is PrivacyValidationException;
        context.Response.StatusCode = isValidation ? StatusCodes.Status400BadRequest : StatusCodes.Status404NotFound;
        context.Response.Headers.CacheControl = "no-store";

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = context.Response.StatusCode,
                Title = isValidation ? "Privacy settings validation error" : "Privacy settings not found"
            }
        });

        return true;
    }
}
