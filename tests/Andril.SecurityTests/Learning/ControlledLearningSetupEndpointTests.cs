using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Authentication;
using Andril.API.Controllers.Learning;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.SecurityTests.Learning;

/// <summary>Actual HTTP and framework pipeline, synthetic dependencies only. Does not establish
/// database atomicity: B2-08 PostgreSQL evidence and B2-10 HTTP/PostgreSQL tests are separate.</summary>
public sealed class ControlledLearningSetupEndpointTests
{
    private const string PrivateMarker = "SYNTHETIC-PRIVATE-LEARNING-MARKER";
    private sealed class State : ILearningSetupAuthorityReader, ILearningSkillCatalogueReader, ILearningSetupMutationRepository
    {
        internal readonly Guid Subject = Guid.NewGuid(), Account = Guid.NewGuid(), Profile = Guid.NewGuid(), Skill = Guid.NewGuid(), Generation = Guid.NewGuid();
        internal bool Denied, Unavailable, ThrowPrivate, Cancel;
        internal int Writes, Reads;
        internal LearningSetupWriteOutcome Outcome = LearningSetupWriteOutcome.Committed;
        internal LearningGoalChange? LastGoal;
        internal LearningSetupSnapshot Snapshot;
        internal string Revision => LearningSetupRevision.FromPersisted(Generation, 0).ToOpaqueToken();
        internal State()
        {
            var p = new LearningPreferences(Account, Profile); var goal = p.CreateGoal(PrivateMarker);
            Snapshot = new(new(Account, Profile), [], [LearningPersonalGoal.CreateFromPreferences(p, goal.Id)]);
        }
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningSetupAccessEvidence?>(Unavailable ? null : new(subject, Account, Profile, Account,
                Denied ? "social" : "learning", operation, true, true, true, true, true, false, false, Generation, Generation));
        }
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LearningSkillCatalogueReadResult.Available(new([new(Skill, true, LearningSkillCatalogueStatus.Active)]), "catalogue"));
        public Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
        {
            Reads++; cancellationToken.ThrowIfCancellationRequested();
            if (ThrowPrivate) throw new IOException(PrivateMarker, new InvalidOperationException(PrivateMarker));
            if (Cancel) throw new OperationCanceledException(PrivateMarker);
            return Task.FromResult(LearningSetupReadResult.Available(Snapshot, Revision));
        }
        public Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision,
            string catalogueRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException("HTTP must retain operation identity.");
        public Task<LearningSetupWriteOutcome> ReplaceWithOperationAsync(Guid operationId, LearningSetupSnapshot replacement,
            string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++;
            Assert.NotEqual(Guid.Empty, operationId); Assert.Equal(Revision, expectedStorageRevision);
            Assert.Equal(Account, replacement.Scope.AccountId); Assert.Equal(Profile, replacement.Scope.LearningProfileId);
            return Task.FromResult(Outcome);
        }
        public Task<LearningSetupWriteOutcome> ChangeGoalAsync(Guid operationId, LearningSetupScope scope, LearningGoalChange change,
            string expectedRevision, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; LastGoal = change; Assert.Equal(Account, scope.AccountId); Assert.Equal(Profile, scope.LearningProfileId); Assert.Equal(Revision, expectedRevision); return Task.FromResult(Outcome); }
    }

    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, State state) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization.ToString() == "Bearer SYNTHETIC-TEST-ONLY"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new("sub", state.Subject.ToString()), new("role", "authenticated"), new("is_anonymous", "false")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }

    private sealed class Capture : ILoggerProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Entries = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);
        public void Dispose() { }
        private sealed class CapturedLogger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }

    private sealed class Host(WebApplication app, HttpClient client, State state, Capture logs) : IAsyncDisposable
    {
        internal HttpClient Client => client;
        internal State State => state;
        internal Capture Logs => logs;
        internal static async Task<Host> Create(bool authenticated = true)
        {
            var state = new State(); var logs = new Capture();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Learning:ControlledTestHost"] = "true" });
            if (OperatingSystem.IsWindows()) builder.Logging.AddFilter<EventLogLoggerProvider>((_, _) => false);
            builder.Logging.AddProvider(logs);
            // appsettings filters Microsoft.AspNetCore at Warning. Request start/finish are
            // Information: explicitly capture this real framework category in this provider
            // only, so unrelated key-ring errors cannot masquerade as effective request capture.
            builder.Logging.AddFilter<Capture>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            builder.Services.AddSingleton(state);
            builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
            builder.Services.AddAuthorization(); builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
            builder.Services.AddSingleton<ILearningSetupAuthorityReader>(state);
            builder.Services.AddSingleton<ILearningSkillCatalogueReader>(state);
            builder.Services.AddSingleton<ILearningSetupRepository>(state);
            builder.Services.AddScoped<ILearningSetupAccessBoundary, LearningSetupAccessBoundary>();
            builder.Services.AddScoped<LearningPreferencesApplicationService>();
            builder.Services.AddProblemDetails(); builder.Services.AddControlledLearningSetupOpenApi();
            var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
            app.MapControlledLearningSetup(); app.MapOpenApi(); await app.StartAsync();
            var client = app.GetTestClient();
            if (authenticated) client.DefaultRequestHeaders.Authorization = new("Bearer", "SYNTHETIC-TEST-ONLY");
            return new(app, client, state, logs);
        }
        internal Task<HttpResponseMessage> Send(string body, string? operation = null, string? path = null, HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Put, path ?? ControlledLearningSetupEndpoints.Root + "/selections")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", operation ?? Guid.NewGuid().ToString());
            return client.SendAsync(request);
        }
        internal string SelectionBody(string? expected = null) => JsonSerializer.Serialize(new
        { expectedRevision = expected ?? state.Revision, teachingSkills = new[] { state.Skill }, learningSkills = new[] { state.Skill } });
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task AuthorizedReadReturnsOnlyOwnedProjectionAndNoStore()
    { await using var h = await Host.Create(); var r = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.True(r.Headers.CacheControl!.NoStore); var json = await r.Content.ReadAsStringAsync(); Assert.Contains(PrivateMarker, json); Assert.DoesNotContain(h.State.Account.ToString(), json); Assert.DoesNotContain(h.State.Profile.ToString(), json); using var doc = JsonDocument.Parse(json); Assert.Equal(h.State.Revision, doc.RootElement.GetProperty("revision").GetString()); }

    [Fact]
    public async Task UnauthenticatedRequestsCannotReachRepository()
    { await using var h = await Host.Create(false); Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root)).StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, (await h.Send(h.SelectionBody())).StatusCode); Assert.Equal(0, h.State.Reads); Assert.Equal(0, h.State.Writes); }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 503)]
    public async Task DeniedOrUnavailableEvidenceDisclosesNothingAndCannotMutate(bool unavailable, int status)
    { await using var h = await Host.Create(); h.State.Denied = !unavailable; h.State.Unavailable = unavailable; var read = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(status, (int)read.StatusCode); Assert.DoesNotContain(PrivateMarker, await read.Content.ReadAsStringAsync()); Assert.Equal(status, (int)(await h.Send(h.SelectionBody())).StatusCode); Assert.Equal(0, h.State.Reads); Assert.Equal(0, h.State.Writes); }

    [Theory]
    [InlineData(LearningSetupWriteOutcome.Committed, 204)]
    [InlineData(LearningSetupWriteOutcome.Denied, 403)]
    [InlineData(LearningSetupWriteOutcome.Unavailable, 503)]
    [InlineData(LearningSetupWriteOutcome.Conflict, 409)]
    [InlineData(LearningSetupWriteOutcome.CommitUnknown, 503)]
    public async Task SelectionOutcomeMappingRetainsIdentityWithoutReplay(LearningSetupWriteOutcome outcome, int status)
    { await using var h = await Host.Create(); h.State.Outcome = outcome; var response = await h.Send(h.SelectionBody()); Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal(1, h.State.Writes); Assert.DoesNotContain(PrivateMarker, await response.Content.ReadAsStringAsync()); }

    [Theory]
    [InlineData("accountId")]
    [InlineData("profileId")]
    [InlineData("module")]
    [InlineData("actorId")]
    [InlineData("unexpected")]
    public async Task ClientOwnershipAndUnknownFieldsAreRejected(string name)
    { await using var h = await Host.Create(); var body = h.SelectionBody().TrimEnd('}') + ",\"" + name + "\":\"" + h.State.Account + "\"}"; Assert.Equal(HttpStatusCode.BadRequest, (await h.Send(body)).StatusCode); Assert.Equal(0, h.State.Writes); }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"expectedRevision\":\"legacy\",\"teachingSkills\":[],\"learningSkills\":[]}")]
    [InlineData("{\"expectedRevision\":null,\"teachingSkills\":[],\"learningSkills\":[]}")]
    [InlineData("{\"expectedRevision\":\"a\",\"expectedRevision\":\"b\",\"teachingSkills\":[],\"learningSkills\":[]}")]
    public async Task MalformedMissingDuplicateOrLegacyPreconditionsNeverWrite(string body)
    { await using var h = await Host.Create(); Assert.Equal(HttpStatusCode.BadRequest, (await h.Send(body)).StatusCode); Assert.Equal(0, h.State.Writes); }

    [Fact]
    public async Task StaleGenerationIsNotRefreshedEvenWhenContentMatches()
    { await using var h = await Host.Create(); var stale = LearningSetupRevision.FromPersisted(Guid.NewGuid(), 0).ToOpaqueToken(); Assert.Equal(HttpStatusCode.Conflict, (await h.Send(h.SelectionBody(stale))).StatusCode); Assert.Equal(0, h.State.Writes); }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidOperationIdentityCannotWrite(string operation)
    { await using var h = await Host.Create(); Assert.Equal(HttpStatusCode.BadRequest, (await h.Send(h.SelectionBody(), operation)).StatusCode); Assert.Equal(0, h.State.Writes); }

    [Theory]
    [InlineData(LearningGoalChangeKind.Create)]
    [InlineData(LearningGoalChangeKind.Edit)]
    [InlineData(LearningGoalChangeKind.Remove)]
    public async Task GoalRoutesResolveServerScopeAndPreserveConditionalCommand(LearningGoalChangeKind kind)
    {
        await using var h = await Host.Create(); var id = Guid.NewGuid();
        var body = kind == LearningGoalChangeKind.Remove ? JsonSerializer.Serialize(new { expectedRevision = h.State.Revision })
            : JsonSerializer.Serialize(new { expectedRevision = h.State.Revision, text = "<script>SYNTHETIC</script>" });
        var path = ControlledLearningSetupEndpoints.Root + "/goals" + (kind == LearningGoalChangeKind.Create ? "" : "/" + id);
        var method = kind == LearningGoalChangeKind.Create ? HttpMethod.Post : kind == LearningGoalChangeKind.Edit ? HttpMethod.Put : HttpMethod.Delete;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send(body, path: path, method: method)).StatusCode);
        Assert.Equal(kind, h.State.LastGoal!.Kind); if (kind != LearningGoalChangeKind.Create) Assert.Equal(id, h.State.LastGoal.GoalId);
    }

    [Fact]
    public async Task FrameworkLoggingCaptureRemainsEffectiveWithoutPrivateExceptionPayloads()
    {
        await using var h = await Host.Create(); h.State.ThrowPrivate = true;
        var response = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(PrivateMarker, await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(h.Logs.Entries); Assert.Contains(h.Logs.Entries, x => x.Contains("Request", StringComparison.Ordinal));
        Assert.All(h.Logs.Entries, x => Assert.DoesNotContain(PrivateMarker, x));
    }

    [Fact]
    public async Task DependencyCancellationDoesNotReturnSuccessOrPrivateDetails()
    { await using var h = await Host.Create(); h.State.Cancel = true; var response = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.DoesNotContain(PrivateMarker, await response.Content.ReadAsStringAsync()); }

    [Fact]
    public async Task OpenApiDescribesOnlyControlledRoutesAndExpectedDtoFields()
    {
        await using var h = await Host.Create(); var response = await h.Client.GetAsync("/openapi/v1.json"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var paths = json.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty(ControlledLearningSetupEndpoints.Root).TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(ControlledLearningSetupEndpoints.Root + "/selections").TryGetProperty("put", out _));
        Assert.True(paths.GetProperty(ControlledLearningSetupEndpoints.Root + "/goals").TryGetProperty("post", out _));
        var goal = paths.GetProperty(ControlledLearningSetupEndpoints.Root + "/goals/{goalId}"); Assert.True(goal.TryGetProperty("put", out _)); Assert.True(goal.TryGetProperty("delete", out _));
        var document = json.RootElement.GetRawText(); Assert.Contains("expectedRevision", document); Assert.Contains("teachingSkills", document); Assert.DoesNotContain("accountId", document); Assert.DoesNotContain("profileId", document);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Testing", false)]
    public async Task MappingRequiresExplicitIsolatedHostAndNeverChangesProduction(string environment, bool optedIn)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Learning:ControlledTestHost"] = optedIn.ToString() });
        await using var app = builder.Build(); Assert.Throws<InvalidOperationException>(() => app.MapControlledLearningSetup());
    }
}
