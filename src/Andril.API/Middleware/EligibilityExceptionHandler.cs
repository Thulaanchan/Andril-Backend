using Andril.Common.Accounts.Eligibility;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class EligibilityExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (EligibilityValidationException or EligibilityAccountMissingException)) return false;
        context.Response.StatusCode = exception is EligibilityValidationException ? 400 : 404;
        context.Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = context.Response.StatusCode,
                Title = exception is EligibilityValidationException ? "Invalid eligibility submission" : "Account not initialized"
            }
        });
        return true;
    }
}
