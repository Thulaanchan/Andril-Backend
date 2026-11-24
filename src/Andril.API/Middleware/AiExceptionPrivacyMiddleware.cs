namespace Andril.API.Middleware;

/// <summary>
/// Unregistered AI-only post-response-start exception privacy boundary. When explicitly composed,
/// place downstream of UseExceptionHandler and upstream of the AI operation. The framework then
/// receives only a sanitized failure for its diagnostics and existing started-response handling.
/// No production activation, response rewrite, retry, success claim or global logging filter.
/// This protects escaping exception payloads, not arbitrary logs already written by dependencies.
/// </summary>
public sealed class AiExceptionPrivacyMiddleware(RequestDelegate next, ILogger<AiExceptionPrivacyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (context.Response.HasStarted &&
            context.Request.Path.StartsWithSegments("/api/v1/ai", StringComparison.OrdinalIgnoreCase))
        {
            // Use fixed classifications only. Never pass the original exception, its type name,
            // message, Data, inner exceptions or request/private content to logging or wrapping.
            var kind = exception switch
            {
                OperationCanceledException => "cancellation",
                IOException => "io",
                _ => "failure"
            };
            var aborted = (exception is OperationCanceledException or IOException) &&
                context.RequestAborted.IsCancellationRequested;
            logger.Log(aborted ? LogLevel.Information : LogLevel.Error,
                new EventId(5105, "AiPostStartFailure"),
                "AI response interrupted after start ({FailureKind}); completion is unverified.", kind);

            // Preserve the categories used by framework request-abort handling, including the
            // original cancellation token. No original exception object is retained as InnerException.
            Exception sanitized = exception switch
            {
                OperationCanceledException cancellation => new OperationCanceledException(
                    "AI response interrupted after start.", cancellation.CancellationToken),
                IOException => new IOException("AI response interrupted after start."),
                _ => new InvalidOperationException("AI response interrupted after start.")
            };
            throw sanitized;
        }
    }
}
