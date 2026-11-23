using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Andril.API.Authentication;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;

namespace Andril.IntegrationTests.Lifecycle;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LifecycleTestRequest(bool? SocialOptIn = null, long? ExpectedRevision = null);

// This entire executable composition is in the test assembly. It cannot be activated
// using production configuration flags. Authentication validates a signed test bearer
// token; the real SupabaseUserVerifier parses a synthetic upstream response in memory.
internal sealed class ControlledLifecycleHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly SymmetricSecurityKey key;
    internal HttpClient Client { get; private set; } = null!;
    internal List<string> SafeLog { get; }
    private ControlledLifecycleHost(WebApplication app, SymmetricSecurityKey key, List<string> log)
    { this.app = app; this.key = key; SafeLog = log; }

    internal static async Task<ControlledLifecycleHost> StartAsync(ControlledLifecycleDatabase db, bool optIn = true, int limit = 50,
        Func<IModuleLifecycleStore, IModuleLifecycleStore>? decorate = null)
    {
        if (!optIn) throw new InvalidOperationException("Explicit integration opt-in is required.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LifecycleIntegration" });
        builder.WebHost.UseTestServer();
        var log = new List<string>();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CaptureLogs(log));
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = "andril-lifecycle-test", ValidateAudience = true,
                ValidAudience = "andril-lifecycle-test", ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi(o =>
        {
          o.AddOperationTransformer((operation, context, _) =>
          {
            if (context.Description.HttpMethod == "POST" && context.Description.RelativePath?.StartsWith("api/v1/account/modules/", StringComparison.Ordinal) == true)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" } });
                if (operation.Parameters.OfType<OpenApiParameter>().FirstOrDefault(p => p.Name == "module") is { } module)
                    module.Schema = new OpenApiSchema { Type = JsonSchemaType.String,
                        Enum = [JsonValue.Create("learning"), JsonValue.Create("hobbies"), JsonValue.Create("social")] };
                var reactivation = context.Description.RelativePath.EndsWith("/reactivation", StringComparison.Ordinal);
                var schema = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    { ["socialOptIn"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Must be explicitly true for Social." } } };
                if (reactivation)
                {
                    schema.Properties["expectedRevision"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Description = "Positive expected revision below Int64.MaxValue." };
                    schema.Required = new HashSet<string> { "expectedRevision" };
                }
                operation.RequestBody = new OpenApiRequestBody { Required = true,
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } } };
            }
            return Task.CompletedTask;
          });
          o.AddDocumentTransformer((document, _, _) =>
          {
            document.Components ??= new();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            { ["testBearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" } };
            document.Security = [new() { [new OpenApiSecuritySchemeReference("testBearer", document)] = [] }];
            return Task.CompletedTask;
          });
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("lifecycle", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue("sub") ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:ProjectUrl"] = "https://synthetic.supabase.co",
            ["SupabaseAuth:SigningAlgorithm"] = "RS256", ["SupabaseIdentity:PublishableKey"] = "sb_publishable_synthetic_test_only"
        }).Build();
        builder.Services.AddSingleton<ISupabaseUserVerifier>(new SupabaseUserVerifier(new HttpClient(new SyntheticIdentity(db)), config, TimeProvider.System));
        builder.Services.AddScoped<IModuleLifecycleStore>(_ => decorate is null ? db.Store() : decorate(db.Store()));
        builder.Services.AddScoped<OfflineModuleLifecycleService>();
        builder.Services.AddSingleton<IPrivateProfileCompletionReader>(new CompletedSyntheticProfile());
        builder.Services.AddScoped<IPrivateModuleAccess, PrivateModuleAccess>();
        var app = builder.Build();
        var host = new ControlledLifecycleHost(app, key, log);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (Exception e) when (e is ModuleLifecycleCommitUnknownException or NpgsqlException or IOException or OperationCanceledException)
            { await Problem(context, 503); }
            catch (ModuleLifecycleException e)
            {
                await Problem(context, e.Denial switch
                {
                    ModuleLifecycleDenial.ConflictingTransition => 409,
                    ModuleLifecycleDenial.EvidenceUnavailable => 503,
                    ModuleLifecycleDenial.UnconfirmedAccount => 403,
                    _ => 403
                });
            }
            catch (Exception e) when (e is JsonException or BadHttpRequestException or ArgumentException)
            { await Problem(context, 400); }
            catch (Exception) { await Problem(context, 503); }
            // Deliberate minimal logging contract: no payloads, headers, IDs, exception
            // details, DOB, policy history, connection strings or bearer/idempotency keys.
            context.RequestServices.GetRequiredService<ILogger<ControlledLifecycleHost>>()
                .LogInformation("Lifecycle status {Status}", context.Response.StatusCode);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        async Task<IResult> Execute(HttpContext context, string module, bool reactivation)
        {
            if (!Andril.Common.Accounts.StarterModules.StarterModules.IsValid(module)) return Results.Problem(statusCode: 400, title: "Invalid request");
            if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var subject) || subject == Guid.Empty) return Results.Unauthorized();
            var verifier = context.RequestServices.GetRequiredService<ISupabaseUserVerifier>();
            var verified = await verifier.VerifyAsync(subject, context.Request.Headers.Authorization.ToString()["Bearer ".Length..], context.RequestAborted);
            if (verified != SupabaseUserVerification.Confirmed)
                return Results.Problem(statusCode: verified == SupabaseUserVerification.Unavailable ? 503 : 403, title: "Authorization unavailable or denied");
            if (context.Request.Headers["Idempotency-Key"].Count != 1
                || !Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var requestKey) || requestKey == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid request");
            if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 400, title: "Invalid request");
            var body = await context.Request.ReadFromJsonAsync<LifecycleTestRequest>(context.RequestAborted);
            if (body is null || (reactivation ? body.ExpectedRevision is not > 0 or long.MaxValue : body.ExpectedRevision is not null))
                return Results.Problem(statusCode: 400, title: "Invalid request");
            if (module == "social" && body.SocialOptIn != true || module != "social" && body.SocialOptIn == true)
                return Results.Problem(statusCode: 403, title: "Activation denied");
            return Results.Ok(await context.RequestServices.GetRequiredService<OfflineModuleLifecycleService>().ExecuteAsync(subject,
                new(module, requestKey, body.SocialOptIn == true, body.ExpectedRevision), Guid.NewGuid(), context.RequestAborted));
        }
        foreach (var reactivation in new[] { false, true })
        {
            var isReactivation = reactivation;
            app.MapPost("/api/v1/account/modules/{module}/" + (reactivation ? "reactivation" : "activation"),
                (HttpContext context, string module) => Execute(context, module, isReactivation))
                .RequireAuthorization().RequireRateLimiting("lifecycle")
                .Produces<ModuleLifecycleResult>()
                .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
                .ProducesProblem(409).ProducesProblem(429).ProducesProblem(503)
                .WithName(reactivation ? "ControlledReactivation" : "ControlledActivation")
                .WithDescription("Isolated test-only lifecycle transition; requires a UUID Idempotency-Key header. Grants no module permissions.");
        }
        app.MapGet("/api/v1/modules/{module}/home", async (string module, IPrivateModuleAccess access, CancellationToken ct) =>
        {
            try { await access.RequireAsync(module, ct); return Results.Ok(); }
            catch (ProfileContractPendingException) { return Results.Problem(statusCode: 403, title: "Access denied"); }
        }).RequireAuthorization();
        app.MapOpenApi();
        await app.StartAsync();
        host.Client = app.GetTestClient();
        return host;
    }
    internal string Token(Guid subject, bool expired = false) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "andril-lifecycle-test", audience: "andril-lifecycle-test", claims: [new Claim("sub", subject.ToString())],
        notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(expired ? -1 : 10),
        signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
    internal void Authenticate(Guid subject) => Client.DefaultRequestHeaders.Authorization = new("Bearer", Token(subject));
    private static async Task Problem(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        await Results.Problem(statusCode: status, title: status == 503 ? "Lifecycle dependency unavailable" : "Lifecycle request denied")
            .ExecuteAsync(context);
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }

    private sealed class CaptureLogs(List<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(log);
        public void Dispose() { }
        private sealed class CaptureLogger(List<string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (log) log.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
            }
        }
    }

    private sealed class CompletedSyntheticProfile : IPrivateProfileCompletionReader
    {
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProfileCompletionStatus(null, true));
    }

    private sealed class SyntheticIdentity(ControlledLifecycleDatabase db) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // No network call: synthetic upstream data passes through the actual source parser.
            var subject = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter!).Subject;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = subject, is_anonymous = false, role = "authenticated", aud = "authenticated", email = "synthetic@example.invalid",
                email_confirmed_at = db.Authority.Confirmed ? DateTimeOffset.UtcNow.AddHours(-1).ToString("O") : null
            })) });
        }
    }
}
