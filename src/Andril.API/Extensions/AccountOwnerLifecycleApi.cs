using System.Data.Common;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Lifecycle;
using Andril.Persistence.Accounts;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AccountOwnerLifecycleRequest(Guid RequestKey, long ExpectedRevision, bool ConfirmDeletion = false);

// Explicit composition only: production startup does not call these methods. Deployment
// needs the approved live authority, restricted grants and consumer activation review.
internal static class AccountOwnerLifecycleApi
{
    internal static IServiceCollection AddAccountOwnerLifecycle(this IServiceCollection services,
        Func<IServiceProvider, IAccountOwnerLifecycleAuthority> authorityFactory)
    {
        ArgumentNullException.ThrowIfNull(authorityFactory);
        services.AddScoped<IAccountOwnerLifecycleAuthority>(sp => authorityFactory(sp)
            ?? throw new InvalidOperationException("Account lifecycle authority is unavailable."));
        services.AddScoped<PostgresAccountOwnerLifecycleStore>();
        services.AddScoped<IAccountOwnerLifecycleStore>(sp => sp.GetRequiredService<PostgresAccountOwnerLifecycleStore>());
        services.AddScoped<AccountOwnerLifecycleService>();
        return services;
    }

    internal static void MapAccountOwnerLifecycle(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/account/lifecycle").RequireAuthorization();
        group.MapGet("", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var subject = RequireSubject(context);
                return Project(await context.RequestServices.GetRequiredService<PostgresAccountOwnerLifecycleStore>()
                    .ReadAsync(subject, context.RequestAborted));
            }
            catch (Exception error) when (error is AccountOwnerLifecycleException or DbException or TimeoutException)
            { return Failure(error); }
        });
        foreach (var operation in Enum.GetValues<AccountOwnerOperation>())
        {
            group.MapPost("/" + operation.ToString().ToLowerInvariant(), async (HttpContext context, AccountOwnerLifecycleRequest request) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                try
                {
                    var service = context.RequestServices.GetRequiredService<AccountOwnerLifecycleService>();
                    return Project(await service.ExecuteAsync(new(operation, request.RequestKey,
                        request.ExpectedRevision, request.ConfirmDeletion), context.RequestAborted));
                }
                catch (Exception error) when (error is AccountOwnerLifecycleException or DbException or TimeoutException)
                { return Failure(error); }
            });
        }
    }

    internal static Guid RequireSubject(HttpContext context)
    {
        var caller = context.RequestServices.GetRequiredService<ICurrentUser>();
        return caller.IsAuthenticated && caller.UserId is Guid subject && subject != Guid.Empty
            ? subject : throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.AuthenticationRequired);
    }

    private static IResult Project(AccountOwnerLifecycleResult result) => Results.Ok(new
    {
        state = result.State.ToString().ToLowerInvariant(), revision = result.Revision, changedAtUtc = result.ChangedAtUtc
    });

    internal static IResult Failure(Exception error)
    {
        var status = error is AccountOwnerLifecycleException lifecycle ? lifecycle.Denial switch
        {
            AccountOwnerLifecycleDenial.AuthenticationRequired => 401,
            AccountOwnerLifecycleDenial.ConfirmationRequired => 400,
            AccountOwnerLifecycleDenial.Denied => 403,
            AccountOwnerLifecycleDenial.Conflict => 409,
            _ => 503
        } : 503;
        return Results.Problem(statusCode: status, title: status == 503 ? "Account lifecycle unavailable" : "Account lifecycle request denied");
    }
}

// Optional admission filter for reviewed consumers. It checks the stored state on
// every request, so an old valid token cannot admit a subsequent protected request
// after deletion/deactivation. In-flight work/stream revocation still needs each
// consumer's approved write/read-release fence; this filter does not claim that.
internal sealed class ActiveAccountOwnerFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var subject = AccountOwnerLifecycleApi.RequireSubject(http);
            var current = await http.RequestServices.GetRequiredService<PostgresAccountOwnerLifecycleStore>()
                .ReadAsync(subject, http.RequestAborted);
            if (!AccountLifecyclePolicy.AllowsProtectedAccess(current.State))
                throw new AccountOwnerLifecycleException(AccountOwnerLifecycleDenial.Denied);
        }
        catch (Exception error) when (error is AccountOwnerLifecycleException or DbException or TimeoutException)
        { return AccountOwnerLifecycleApi.Failure(error); }
        return await next(context);
    }
}
