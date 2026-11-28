using System.Text.Json.Serialization;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

/// <summary>Opt-in private owner CV routes. No hosted migrations or live configuration changes.</summary>
public static class LearningPrivateCvRuntimeExtensions
{
    private sealed record Enabled;
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CreateCvRequest(CvTemplate? Template);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ConsentRequest(bool? IncludeContactInformation);

    public static IServiceCollection AddVerifiedLearningPrivateCv(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Learning:PrivateCv:Enabled")) return services;
        var role = configuration["Learning:PrivateCv:RuntimeRole"];
        if (string.IsNullOrWhiteSpace(role)) throw new InvalidOperationException("Private Learning CVs require a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningCvRepository>(sp => LearningCvPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),
            sp.GetRequiredService<ICurrentUser>(), sp.GetRequiredService<IPrivateProfileService>(), role));
        return services;
    }

    public static WebApplication MapVerifiedLearningPrivateCv(this WebApplication app)
    {
        if (app.Services.GetService<Enabled>() is null) return app;
        app.MapControlledStructuredCv();
        var group = app.MapGroup("/api/learning/cvs").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        group.MapPost("", (HttpRequest http, ILearningCvRepository store, CancellationToken ct) => Safe(async () =>
        {
            if (!http.HasJsonContentType()) return Results.StatusCode(400);
            var request = await http.ReadFromJsonAsync<CreateCvRequest>(ct);
            if (request?.Template is not { } template || !Enum.IsDefined(template)) return Results.StatusCode(400);
            var result = await store.CreateAsync(template, ct);
            return result.Cv is null ? Results.StatusCode(Status(result.State)) : Results.Json(new { id = result.Cv.Id });
        }, ct)).Accepts<CreateCvRequest>("application/json");
        group.MapGet("", (ILearningCvRepository store, CancellationToken ct) => Safe(async () =>
        {
            var result = await store.ListAsync(ct);
            return result.State == CvOperationState.Available ? Results.Json(new { ids = result.CvIds }) : Results.StatusCode(Status(result.State));
        }, ct));
        group.MapPut("/{id:guid}/contact-consent", (Guid id, HttpRequest http, ILearningCvRepository store, CancellationToken ct) => Safe(async () =>
        {
            if (!http.HasJsonContentType()) return Results.StatusCode(400);
            var request = await http.ReadFromJsonAsync<ConsentRequest>(ct);
            if (request?.IncludeContactInformation is not { } include) return Results.StatusCode(400);
            var result = await store.SaveAsync(id, CvSaveChange.ContactConsent(include), ct);
            return Results.StatusCode(Status(result.State));
        }, ct)).Accepts<ConsentRequest>("application/json");
        return app;
    }

    private static int Status(CvOperationState state) => state switch
    { CvOperationState.Available => 200, CvOperationState.NotFound => 404, CvOperationState.Denied => 403, CvOperationState.Conflict => 409, _ => 503 };
    private static async Task<IResult> Safe(Func<Task<IResult>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return Results.StatusCode(400); }
        catch (JsonException) { return Results.StatusCode(400); }
        catch (BadHttpRequestException) { return Results.StatusCode(400); }
        catch (Exception) { return Results.StatusCode(503); }
    }
}
