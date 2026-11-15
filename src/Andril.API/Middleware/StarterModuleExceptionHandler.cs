using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class StarterModuleExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            StarterModuleValidationException => (400, "Select one valid module; Social requires explicit opt-in"),
            StarterModuleAccountMissingException => (404, "Account not initialized"),
            StarterModulePrerequisitesException => (409, "Complete demographics and current required policy actions first"),
            StarterModuleIneligibleException => (403, "Not eligible for the selected module"),
            StarterModuleConflictException => (409, "Starter module already selected; changing it is not supported"),
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
