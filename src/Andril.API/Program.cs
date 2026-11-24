using System.Net;
using Andril.API.Controllers.Common;
using Andril.API.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSupabaseAuthentication(builder.Configuration);
builder.Services.AddAccounts(builder.Configuration);
builder.Services.AddNotifications();
builder.Services.AddSupabaseIdentityVerification(builder.Configuration);
builder.Services.AddModerationReportAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
// Apply even to authentication challenges, redirects and model-binding failures.
app.Use(async (context, next) =>
{
    if (Andril.API.Authentication.IdentityProtectedRoutes.Contains(context.Request.Path))
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.CacheControl.ToString().Equals("private, no-store", StringComparison.Ordinal))
                context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    await next(context);
});
app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if ((path.StartsWithSegments("/swagger") || path.StartsWithSegments("/openapi"))
            && (context.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local)
                || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || !string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    });

    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "ANDRIL Backend API v1");
    });
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<Andril.API.Middleware.ConfirmedAccountIdentityMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("HealthCheck")
   .AllowAnonymous();
app.Run();

public partial class Program { }
