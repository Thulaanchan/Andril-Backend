using Andril.Common.Accounts.Onboarding;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class OnboardingExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OnboardingDependencyException) return false;
        context.Response.StatusCode = 503;
        context.Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = 503, Title = "Onboarding status unavailable" }
        });
        return true;
    }
}
