using Andril.Common.Accounts.Preferences;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class LanguagePreferenceExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (LanguagePreferenceValidationException or LanguagePreferenceAccountMissingException)) return false;
        var invalid = exception is LanguagePreferenceValidationException;
        context.Response.StatusCode = invalid ? 400 : 404;
        context.Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = context.Response.StatusCode,
                Title = invalid ? "Invalid language preference" : "Account not initialized"
            }
        });
        return true;
    }
}
