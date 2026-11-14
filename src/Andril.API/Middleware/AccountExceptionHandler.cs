using Andril.Common.Accounts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class AccountExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (AccountStoreUnavailableException or AccountAuthenticationRequiredException)) return false;
        var status = exception is AccountAuthenticationRequiredException ? 401 : 503;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == 401 ? "Authentication required" : "Account service unavailable"
            }
        });
        return true;
    }
}
