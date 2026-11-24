using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Andril.API.Authentication;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
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

    internal static Task<ControlledLifecycleHost> StartAsync(ControlledLifecycleDatabase db, bool optIn = true, int limit = 50,
        Func<IModuleLifecycleStore, IModuleLifecycleStore>? decorate = null) =>
        StartAsync(() => decorate is null ? db.Store(enableDeactivation: true) : decorate(db.Store(enableDeactivation: true)),
            () => db.Authority.Confirmed, optIn, limit,
            () => db.Store(), (subject, module, ct) => db.Store().FindScopedAccountAsync(subject, module, ct));

    // This overload keeps the signed-bearer HTTP boundary independently runnable without PostgreSQL.
    internal static async Task<ControlledLifecycleHost> StartAsync(Func<IModuleLifecycleStore> store,
        Func<bool> isConfirmed, bool optIn = true, int limit = 50,
        Func<IModuleNavigationReader>? statusReader = null,
        Func<Guid, string, CancellationToken, Task<Account?>>? findAccount = null)
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
                var deactivation = context.Description.RelativePath.EndsWith("/deactivation", StringComparison.Ordinal);
                var schema = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    { ["socialOptIn"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Must be explicitly true for Social." } } };
                if (deactivation) schema.Properties.Clear();
                if (reactivation || deactivation)
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
            o.OnRejected = async (context, _) => await Problem(context.HttpContext, 429);
            // Test-host source budget only. Ignore caller-supplied forwarding headers.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "test-server",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("lifecycle", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue("sub") ?? "anonymous", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:ProjectUrl"] = "https://synthetic.supabase.co",
            ["SupabaseAuth:SigningAlgorithm"] = "RS256", ["SupabaseIdentity:PublishableKey"] = "sb_publishable_synthetic_test_only"
        }).Build();
        builder.Services.AddSingleton<ISupabaseUserVerifier>(new SupabaseUserVerifier(new HttpClient(new SyntheticIdentity(isConfirmed)), config, TimeProvider.System));
        builder.Services.AddScoped<IModuleLifecycleStore>(_ => store());
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
                    ModuleLifecycleDenial.EvidenceUnavailable or ModuleLifecycleDenial.StaleEvidence => 503,
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
        app.UseStatusCodePages(async context => await Problem(context.HttpContext, context.HttpContext.Response.StatusCode));
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        async Task<IResult> Execute(HttpContext context, string module, bool reactivation)
        {
            if (!Andril.Common.Accounts.StarterModules.StarterModules.IsValid(module)) return Results.Problem(statusCode: 400, title: "Invalid request");
            if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var subject) || subject == Guid.Empty) return Results.Unauthorized();
            if (context.Request.Headers["Idempotency-Key"].Count != 1
                || !Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var requestKey) || requestKey == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid request");
            var body = await ReadTransitionRequestAsync(context.Request, reactivation, context.RequestAborted);
            if (body is null)
                return Results.Problem(statusCode: 400, title: "Invalid request");

            var verified = await VerifyIdentityAsync(context, subject);
            if (verified != SupabaseUserVerification.Confirmed)
                return IdentityFailure(verified);
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
        app.MapPost("/api/v1/account/modules/{module}/deactivation", async (HttpContext context, string module) =>
        {
            if (!Andril.Common.Accounts.StarterModules.StarterModules.IsValid(module))
                return Results.Problem(statusCode: 400, title: "Invalid request");
            if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var subject) || subject == Guid.Empty)
                return Results.Unauthorized();
            var keys = context.Request.Headers["Idempotency-Key"].Select(value => value ?? "").ToArray();
            if (keys.Length != 1 || !Guid.TryParseExact(keys[0], "D", out var key) || key == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Invalid request");
            using var body = await ReadBodyAsync(context.Request, context.RequestAborted);
            if (body is null || DeactivateModuleRequest.Parse(body.RootElement) is null)
                return Results.Problem(statusCode: 400, title: "Invalid request");
            var verified = await VerifyIdentityAsync(context, subject);
            if (verified != SupabaseUserVerification.Confirmed)
                return IdentityFailure(verified);
            var contracts = CreateContracts(context, subject);
            return contracts is null ? Results.Problem(statusCode: 503, title: "Lifecycle dependency unavailable")
                : ToHttp(await contracts.DeactivateAsync(module, keys, body.RootElement, Guid.NewGuid(), context.RequestAborted));
        }).RequireAuthorization().RequireRateLimiting("lifecycle")
            .Produces<LifecycleDeactivationResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(409).ProducesProblem(429).ProducesProblem(503).WithName("ControlledDeactivation");

        app.MapGet("/api/v1/account/modules", async (HttpContext context) =>
        {
            if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var subject) || subject == Guid.Empty)
                return Results.Unauthorized();
            var verified = await VerifyIdentityAsync(context, subject);
            if (verified != SupabaseUserVerification.Confirmed)
                return IdentityFailure(verified);
            var contracts = CreateContracts(context, subject);
            return contracts is null ? Results.Problem(statusCode: 503, title: "Lifecycle dependency unavailable")
                : ToHttp(await contracts.ReadStatusesAsync(context.RequestAborted));
        }).RequireAuthorization().RequireRateLimiting("lifecycle")
            .Produces<LifecycleModuleStatuses>().ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(429).ProducesProblem(503).WithName("ControlledModuleStatuses");

        ControlledLifecycleApiContracts? CreateContracts(HttpContext context, Guid subject)
        {
            if (statusReader is null || findAccount is null)
                return null;
            var actor = new ConfirmedActor(subject);
            return new(actor, actor, findAccount, context.RequestServices.GetRequiredService<OfflineModuleLifecycleService>(),
                new(actor, statusReader()));
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
    private sealed record TransitionRequest(bool? SocialOptIn, long? ExpectedRevision);

    private static async Task<TransitionRequest?> ReadTransitionRequestAsync(HttpRequest request, bool reactivation, CancellationToken ct)
    {
        using var document = await ReadBodyAsync(request, ct);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        bool? socialOptIn = null;
        long? expectedRevision = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("socialOptIn") && socialOptIn is null &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                socialOptIn = property.Value.GetBoolean();
            else if (reactivation && property.NameEquals("expectedRevision") && expectedRevision is null &&
                property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var revision) &&
                revision is > 0 and < long.MaxValue)
                expectedRevision = revision;
            else
                return null;
        }

        return reactivation && expectedRevision is null ? null : new(socialOptIn, expectedRevision);
    }

    private static Task<SupabaseUserVerification> VerifyIdentityAsync(HttpContext context, Guid subject) =>
        context.RequestServices.GetRequiredService<ISupabaseUserVerifier>().VerifyAsync(
            subject, context.Request.Headers.Authorization.ToString()["Bearer ".Length..], context.RequestAborted);

    private static IResult IdentityFailure(SupabaseUserVerification result) => result switch
    {
        SupabaseUserVerification.Unauthorized => Results.Unauthorized(),
        SupabaseUserVerification.Unavailable => Results.Problem(statusCode: 503, title: "Lifecycle dependency unavailable"),
        _ => Results.Problem(statusCode: 403, title: "Lifecycle request denied")
    };

    private static IResult ToHttp<T>(LifecycleApiResult<T> result) where T : class =>
        result.Body is { } body ? Results.Ok(body) :
        Results.Problem(statusCode: (int)result.Status, title: result.Status == HttpStatusCode.ServiceUnavailable
            ? "Lifecycle dependency unavailable" : "Lifecycle request denied");

    private sealed class ConfirmedActor(Guid subject) : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid candidate) => candidate == subject;
    }

    private static async Task<JsonDocument?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        const int maxBytes = 4096; // Controlled test-host limit.
        if (!request.HasJsonContentType() || request.ContentLength > maxBytes)
            return null;

        // Bound chunked/lengthless requests as well as those that declare Content-Length.
        var buffer = new byte[maxBytes + 1];
        var count = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct);
        if (count > maxBytes)
            return null;

        return JsonDocument.Parse(buffer.AsMemory(0, count));
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

    private sealed class SyntheticIdentity(Func<bool> isConfirmed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // No network call: synthetic upstream data passes through the actual source parser.
            var subject = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter!).Subject;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = subject, is_anonymous = false, role = "authenticated", aud = "authenticated", email = "synthetic@example.invalid",
                email_confirmed_at = isConfirmed() ? DateTimeOffset.UtcNow.AddHours(-1).ToString("O") : null
            })) });
        }
    }
}
