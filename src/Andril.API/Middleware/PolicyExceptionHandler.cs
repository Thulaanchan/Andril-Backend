using Andril.Common.Accounts.Policies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class PolicyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            PolicyValidationException => (400, "Both required policy actions and valid versions are required"),
            PolicyAccountMissingException => (404, "Account not initialized"),
            PolicyVersionConflictException => (409, "Policy versions changed; retrieve current documents and review them"),
            PolicyConfigurationException => (503, "Policy documents unavailable"),
            _ => (0, "")
        };
        if (status == 0) return false;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context, ProblemDetails = new ProblemDetails { Status = status, Title = title }
        });
        return true;
    }
}
