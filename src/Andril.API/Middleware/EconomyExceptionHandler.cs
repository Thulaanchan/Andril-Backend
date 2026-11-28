using Andril.Economy.VirtualGifts;
using Andril.Economy.Wallet;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class EconomyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (CoinDomainException or InvalidGiftOperationException))
            return false;

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Headers.CacheControl = "no-store";

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Economy operation error",
                Detail = exception.Message
            }
        });

        return true;
    }
}
