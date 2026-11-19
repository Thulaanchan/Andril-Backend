using System.Net.Http.Headers;
using Andril.API.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Middleware;

public sealed class ConfirmedAccountIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISupabaseUserVerifier verifier, IProblemDetailsService problems)
    {
        // Other members' routes retain their existing authorization behavior.
        if (!IdentityProtectedRoutes.Contains(context.Request.Path)
            || VerifiedUserSubject.GetId(context.User) is not Guid subject)
        {
            await next(context);
            return;
        }
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        var result = await verifier.VerifyAsync(subject, header.Parameter, context.RequestAborted);
        if (result == SupabaseUserVerification.Confirmed)
        {
            context.Features.Set(new ConfirmedIdentityFeature(subject));
            await next(context);
            return;
        }
        var status = result == SupabaseUserVerification.Unconfirmed ? 403 : result == SupabaseUserVerification.Unauthorized ? 401 : 503;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (status == 401)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        var problem = new ProblemDetails { Status = status,
            Title = status == 403 ? "Email confirmation required" : "Identity verification unavailable" };
        if (status == 403 && IdentityProtectedRoutes.IsOnboarding(context.Request.Path))
            problem.Extensions["nextStep"] = "verify_identity";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem
        });
    }
}
