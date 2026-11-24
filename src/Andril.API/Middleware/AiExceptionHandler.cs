using Andril.AI.DTOs;
using Andril.AI.Policies;
using Andril.Common.Accounts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

/// <summary>Unregistered proposed AI-only ProblemDetails mapping. Restricts broad exception
/// handling to the reserved /api/v1/ai segment and never alters other members' errors. Public
/// status/codes need API/client review before activation. No exception message/inner data/body,
/// stack, request content, private identifier or trace detail is copied into the response.
/// Framework/pipeline logging is separate and requires independent privacy review.</summary>
public sealed class AiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/ai", StringComparison.OrdinalIgnoreCase) || context.Response.HasStarted)
            return false;
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            // The peer is gone: do not try writing a response using a cancelled request token.
            // Unknown durable work must be reconciled; no retry/refund/success is reported.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            return true;
        }
        var (status, title, code) = exception switch
        {
            AiChatValidationException or ArgumentException => (400, "Invalid AI request", AiApiErrorCodes.InvalidRequest),
            AccountAuthenticationRequiredException => (401, "Authentication required", AiApiErrorCodes.AuthenticationRequired),
            UnauthorizedAccessException => (403, "AI access unavailable", AiApiErrorCodes.AccessUnavailable),
            AccountStoreUnavailableException or InvalidOperationException => (503, "AI integration unavailable", AiApiErrorCodes.IntegrationUnavailable),
            OperationCanceledException => (503, "AI request interrupted; reconciliation required", AiApiErrorCodes.Cancelled),
            _ => (500, "AI request could not be completed", AiApiErrorCodes.UnexpectedFailure)
        };
        // Expiry/missing/foreign denial share one generic access response. Rate/quota/budget,
        // conflicts and safety outcomes are returned by service enums, not distinct exception
        // types. Do not guess them from exception text or report transport cancellation as zero cost.
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
        return true;
    }
}
