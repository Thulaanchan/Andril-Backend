using Andril.API.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSupabaseAuthentication(builder.Configuration);
builder.Services.AddAccounts(builder.Configuration);
builder.Services.AddSupabaseIdentityVerification(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
// Apply even to authentication challenges, redirects and model-binding failures.
app.Use(async (context, next) =>
{
    if (Andril.API.Authentication.IdentityProtectedRoutes.Contains(context.Request.Path))
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    await next(context);
});
app.UseHttpsRedirection();
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
