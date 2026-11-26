using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Andril.API.Authentication;
using Andril.API.Controllers.Learning;
using Andril.Learning.CV;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class ProjectImportHttpPostgresTests
{
    private sealed class Host : IAsyncDisposable
    {
        internal ProjectImportPostgresFixture Fixture { get; private set; } = null!;
        internal HttpClient Client { get; private set; } = null!;
        internal readonly ConcurrentQueue<string> Logs = new();
        private WebApplication? app;
        private string evidenceName = string.Empty;
        private readonly SymmetricSecurityKey key = new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        internal static async Task<Host> Create(Func<string, CancellationToken, Task>? checkpoint = null,
            [System.Runtime.CompilerServices.CallerMemberName] string caseName = "")
        {
            var h = new Host { Fixture = await ProjectImportPostgresFixture.Create(), evidenceName = caseName };
            try
            {
                var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" }); b.WebHost.UseTestServer();
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Learning:ControlledTestHost"] = "true" });
                if (OperatingSystem.IsWindows()) b.Logging.AddFilter<EventLogLoggerProvider>((_, _) => false);
                b.Logging.AddProvider(new Capture(h.Logs)); b.Logging.AddFilter<Capture>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
                b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
                {
                    o.MapInboundClaims = false; o.IncludeErrorDetails = false;
                    o.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = "andril-import-test", ValidateAudience = true, ValidAudience = "andril-import-test", ValidateLifetime = true, ClockSkew = TimeSpan.Zero, ValidateIssuerSigningKey = true, IssuerSigningKey = h.key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256] };
                });
                b.Services.AddAuthorization(); b.Services.AddHttpContextAccessor(); b.Services.AddProblemDetails(); b.Services.AddControlledLearningSetupOpenApi();
                b.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
                b.Services.AddScoped<ILearningProjectImportRepository>(sp => h.Fixture.Repository(checkpoint: checkpoint, currentUser: sp.GetRequiredService<ICurrentUser>()));
                b.Services.AddScoped<LearningProjectImportApplicationService>();
                h.app = b.Build(); h.app.UseAuthentication(); h.app.UseAuthorization(); h.app.MapControlledLearningProjectImport(); h.app.MapOpenApi(); await h.app.StartAsync();
                h.Client = h.app.GetTestClient(); h.Authenticate(h.Fixture.Owner.Subject); return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        internal void Authenticate(Guid subject)
        {
            var token = new JwtSecurityToken("andril-import-test", "andril-import-test", [new Claim(JwtRegisteredClaimNames.Sub, subject.ToString()), new Claim("role", "authenticated"), new Claim("is_anonymous", "false")], expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            try
            {
                if (app is not null) await app.DisposeAsync();
                var evidence = Environment.GetEnvironmentVariable("ANDRIL_IMPORT_HTTP_EVIDENCE_DIR");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    // Only framework method/path/status lines; never bodies, headers, tokens,
                    // reviewed text or exception payloads. Opaque document/review IDs are redacted.
                    var lines = Logs.Where(x => x.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics ", StringComparison.Ordinal) && x.Contains("Request finished", StringComparison.Ordinal))
                        .Select(x => System.Text.RegularExpressions.Regex.Replace(x, @"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b", "[id]"));
                    await File.WriteAllLinesAsync(Path.Combine(evidence, evidenceName + "-" + Guid.NewGuid().ToString("N") + ".txt"), lines);
                }
            }
            finally { if (Fixture is not null) await Fixture.DisposeAsync(); }
        }
    }
    private sealed class Capture(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(entries, categoryName);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> entries, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => entries.Enqueue(category + " " + formatter(state, exception) + " " + exception);
        }
    }
    private static string Reviews => ControlledLearningProjectImportEndpoints.Root + "/reviews";
    private static string Confirm(Guid id) => ControlledLearningProjectImportEndpoints.Root + "/" + id + "/confirm";
    private static async Task<ProjectImportReviewResponse> RequireReview(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<ProjectImportReviewResponse>();
        Assert.NotNull(review); Assert.NotEqual(Guid.Empty, review.ReviewId); Assert.NotNull(review.Projects);
        return review;
    }
    [LocalPostgresFact]
    public async Task AuthenticatedOwnerCanReviewAndExplicitlyConfirmRealDatabaseImport()
    {
        await using var h = await Host.Create(); var s = await ProjectImportPostgresTests.Data(h.Fixture);
        var response = await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var r = await RequireReview(response); Assert.Equal(s.A.Title, Assert.Single(r.Projects).Title);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync(Confirm(r.ReviewId), new ProjectImportConfirmRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.PostAsJsonAsync(Confirm(r.ReviewId), new ProjectImportConfirmRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync(Confirm(r.ReviewId), new ProjectImportConfirmRequest(true))).StatusCode);
    }
    [LocalPostgresFact]
    public async Task AnonymousAndForeignSubjectCannotUseActorFieldsToImport()
    {
        await using var h = await Host.Create(); var s = await ProjectImportPostgresTests.Data(h.Fixture); h.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id]))).StatusCode);
        h.Authenticate(Guid.NewGuid()); var foreign = await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode); Assert.DoesNotContain(s.A.Title!, await foreign.Content.ReadAsStringAsync());
        h.Authenticate(h.Fixture.Owner.Subject); var forged = await h.Client.PostAsJsonAsync(Reviews, new { s.Portfolio.Id, PortfolioId = s.Portfolio.Id, CvId = s.Cv.Id, ProjectIds = new[] { s.A.Id }, AccountId = h.Fixture.Owner.Account }); Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }
    [LocalPostgresFact]
    public async Task StaleReviewAndInactiveLifecycleHaveSanitizedConflictAndDenialResponses()
    {
        await using var h = await Host.Create(); var s = await ProjectImportPostgresTests.Data(h.Fixture); var response = await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); var r = await RequireReview(response);
        await h.Fixture.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.A.Id, "SYNTHETIC-CHANGED", "private"); var stale = await h.Client.PostAsJsonAsync(Confirm(r.ReviewId), new ProjectImportConfirmRequest(true)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.DoesNotContain("SYNTHETIC", await stale.Content.ReadAsStringAsync());
        await h.Fixture.Shared.Shared.Shared.Lifecycle("learning", false); Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsJsonAsync(Confirm(r.ReviewId), new ProjectImportConfirmRequest(true))).StatusCode);
    }
    [LocalPostgresFact]
    public async Task InfrastructureErrorsStayConfidentialWhileFrameworkLoggingRemainsEffective()
    {
        const string secret = "SYNTHETIC-IMPORT-PRIVATE-EXCEPTION";
        await using var h = await Host.Create((_, _) => { var e = new InvalidOperationException(secret, new Exception(secret + "-INNER")); e.Data[secret] = secret; return Task.FromException(e); }); var s = await ProjectImportPostgresTests.Data(h.Fixture);
        var r = await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode); Assert.DoesNotContain(secret, await r.Content.ReadAsStringAsync());
        Assert.Contains(h.Logs, x => x.Contains("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)); Assert.Contains(h.Logs, x => x.Contains("Controlled Learning import dependency unavailable.", StringComparison.Ordinal)); Assert.All(h.Logs, x => Assert.DoesNotContain(secret, x));
    }
    [LocalPostgresFact]
    public async Task OpenApiDescribesOnlyExplicitSelectionAndConfirmationNotClientIdentity()
    {
        await using var h = await Host.Create(); var r = await h.Client.GetAsync("/openapi/v1.json"); Assert.Equal(HttpStatusCode.OK, r.StatusCode); var json = await r.Content.ReadAsStringAsync();
        Assert.Contains(Reviews, json); Assert.Contains("/api/v1/learning/cv-imports/{reviewId}/confirm", json); Assert.Contains("ProjectImportReviewRequest", json); Assert.Contains("ProjectImportConfirmRequest", json); Assert.DoesNotContain("ownerLearningProfileId", json);
    }
    [LocalPostgresFact]
    public async Task UnavailableAuthorityAndInvalidSelectionsReturnSafeResponsesWithoutMutation()
    {
        await using var h = await Host.Create(); var s = await ProjectImportPostgresTests.Data(h.Fixture);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, []))).StatusCode);
        h.Fixture.Owner.Unavailable = true; var r = await h.Client.PostAsJsonAsync(Reviews, new ProjectImportReviewRequest(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode); Assert.DoesNotContain(s.A.Title!, await r.Content.ReadAsStringAsync());
    }
}
