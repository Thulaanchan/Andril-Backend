using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.API.Authentication;
using Andril.API.Controllers.Learning;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Andril.IntegrationTests.Database;

/// <summary>Actual HTTP -> application -> restricted-role EF/PostgreSQL composition. Only
/// standing/session/catalogue authority is synthetic; signed bearer validation, ownership,
/// lifecycle, RLS, Serializable coordination, revision triggers and commits are real.</summary>
internal sealed class ControlledLearningHttpPostgresHost : IAsyncDisposable
{
    internal const string PrivateMarker = "SYNTHETIC-PRIVATE-B210-EXCEPTION";
    internal readonly Guid Subject = Guid.NewGuid(), Account = Guid.NewGuid(), Profile = Guid.NewGuid();
    internal readonly Guid Skill = Guid.NewGuid(), OtherSkill = Guid.NewGuid(), EvidenceVersion = Guid.NewGuid();
    internal LearningSetupScope Scope => new(Account, Profile);
    internal bool Revoked, Unavailable, WrongModule, Restricted, ThrowPrivate;
    internal bool LoseCommitAcknowledgement;
    internal Func<string, CancellationToken, Task>? Checkpoint;
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs = new();
    internal LocalAccountDatabase Fixture { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;
    private WebApplication? app;
    private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32));
    private string? runtimeConnection;
    private string? runtimeRole;
    private bool disposed;

    private sealed class Authority(ControlledLearningHttpPostgresHost host) : ILearningSetupAuthorityReader, ILearningSkillCatalogueReader
    {
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.ThrowPrivate) throw new IOException(PrivateMarker, new InvalidOperationException(PrivateMarker));
            return Task.FromResult<LearningSetupAccessEvidence?>(host.Unavailable ? null : new(subject, host.Account,
                host.Profile, host.Account, host.WrongModule ? "social" : "learning", operation, true, true,
                !host.Restricted, true, true, host.Revoked, false, host.EvidenceVersion, host.EvidenceVersion));
        }
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(host.Unavailable ? LearningSkillCatalogueReadResult.Unavailable() : LearningSkillCatalogueReadResult.Available(
                new([new(host.Skill, true, LearningSkillCatalogueStatus.Active), new(host.OtherSkill, true, LearningSkillCatalogueStatus.Active)]), "b210-catalogue"));
        }
    }

    private sealed class Capture(ControlledLearningHttpPostgresHost host) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(host);
        public void Dispose() { }
        private sealed class CapturedLogger(ControlledLearningHttpPostgresHost host) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                host.Logs.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }

    private sealed class CommitAcknowledgement(ControlledLearningHttpPostgresHost host) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!host.LoseCommitAcknowledgement) return Task.CompletedTask;
            host.LoseCommitAcknowledgement = false;
            return Task.FromException(new IOException(PrivateMarker));
        }
    }

    internal static async Task<ControlledLearningHttpPostgresHost> Create()
    {
        var h = new ControlledLearningHttpPostgresHost { Fixture = await LocalAccountDatabase.CreateAsync() };
        try
        {
            await LearningPostgresFixture.Migrate(h.Fixture);
            await LearningPostgresFixture.Profile(h.Fixture, h.Scope, h.Subject);
            await using (var db = LearningPostgresFixture.Open(h.Fixture))
            {
                h.runtimeRole = "andril_learning_http_" + Guid.NewGuid().ToString("N");
                var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"CREATE ROLE {h.runtimeRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)db.Database.GetDbConnection());
                // Fixed prefixes plus server random identifiers/credentials only. Never log DDL.
                await sql.ExecuteNonQueryAsync();
                sql.CommandText = $"GRANT USAGE ON SCHEMA andril_learning TO {h.runtimeRole}; GRANT SELECT ON andril_learning.learning_skill_selections,andril_learning.learning_personal_goals,andril_learning.learning_profile_revisions,andril_learning.learning_mutation_receipts TO {h.runtimeRole}; GRANT INSERT,DELETE ON andril_learning.learning_skill_selections,andril_learning.learning_personal_goals TO {h.runtimeRole}; GRANT UPDATE(text) ON andril_learning.learning_personal_goals TO {h.runtimeRole}; GRANT INSERT ON andril_learning.learning_mutation_receipts TO {h.runtimeRole}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_profile_revision(uuid,uuid),andril_learning.lock_learning_owners(uuid,uuid,uuid) TO {h.runtimeRole}";
                await sql.ExecuteNonQueryAsync();
                h.runtimeConnection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
                { Username = h.runtimeRole, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Learning:ControlledTestHost"] = "true" });
            if (OperatingSystem.IsWindows()) builder.Logging.AddFilter<EventLogLoggerProvider>((_, _) => false);
            builder.Logging.AddProvider(new Capture(h));
            builder.Logging.AddFilter<Capture>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; o.IncludeErrorDetails = false;
                o.TokenValidationParameters = new()
                {
                    ValidateIssuer = true, ValidIssuer = "andril-learning-test", ValidateAudience = true, ValidAudience = "andril-learning-test",
                    ValidateLifetime = true, ClockSkew = TimeSpan.Zero, ValidateIssuerSigningKey = true, IssuerSigningKey = h.key,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            });
            builder.Services.AddAuthorization(); builder.Services.AddHttpContextAccessor(); builder.Services.AddProblemDetails();
            builder.Services.AddControlledLearningSetupOpenApi();
            var authority = new Authority(h);
            builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
            builder.Services.AddSingleton<ILearningSetupAuthorityReader>(authority);
            builder.Services.AddSingleton<ILearningSkillCatalogueReader>(authority);
            builder.Services.AddScoped<ILearningSetupAccessBoundary, LearningSetupAccessBoundary>();
            builder.Services.AddScoped<ILearningSetupRepository>(sp => new IsolatedLearningSetupRepository(
                new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(h.runtimeConnection).AddInterceptors(new CommitAcknowledgement(h)).Options,
                sp.GetRequiredService<ICurrentUser>(), sp.GetRequiredService<ILearningSetupAccessBoundary>(), authority)
            { TestCheckpoint = (phase, ct) => h.Checkpoint?.Invoke(phase, ct) ?? Task.CompletedTask });
            builder.Services.AddScoped<LearningPreferencesApplicationService>();
            h.app = builder.Build(); h.app.UseAuthentication(); h.app.UseAuthorization();
            h.app.MapControlledLearningSetup(); h.app.MapOpenApi(); await h.app.StartAsync();
            h.Client = h.app.GetTestClient(); h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", h.Token());
            return h;
        }
        catch { await h.DisposeAsync(); throw; }
    }

    internal string Token(Guid? subject = null, string issuer = "andril-learning-test", bool expired = false, bool wrongSignature = false, bool anonymous = false)
    {
        var signingKey = wrongSignature ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) : key;
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, "andril-learning-test",
            [new Claim("sub", (subject ?? Subject).ToString()), new Claim("role", "authenticated"), new Claim("is_anonymous", anonymous ? "true" : "false")],
            now.AddMinutes(-10), expired ? now.AddMinutes(-1) : now.AddMinutes(10), new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
    }

    internal async Task<LearningSetupResponse> Read()
    {
        using var response = await Client.GetAsync(ControlledLearningSetupEndpoints.Root);
        response.EnsureSuccessStatusCode();
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<LearningSetupResponse>(response.Content))!;
    }

    internal Task<HttpResponseMessage> Send(HttpMethod method, string suffix, object body, Guid? operation = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(method, ControlledLearningSetupEndpoints.Root + suffix)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", (operation ?? Guid.NewGuid()).ToString());
        return Client.SendAsync(request, ct);
    }

    internal Task<HttpResponseMessage> Select(string revision, Guid[] teaching, Guid[] learning, Guid? operation = null, CancellationToken ct = default) =>
        Send(HttpMethod.Put, "/selections", new { expectedRevision = revision, teachingSkills = teaching, learningSkills = learning }, operation, ct);

    internal async Task OwnerSql(FormattableString sql)
    {
        await using var db = LearningPostgresFixture.Open(Fixture);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Subject.ToString()},true),set_config('andril.account',{Account.ToString()},true),set_config('andril.module','learning',true)");
        await db.Database.ExecuteSqlInterpolatedAsync(sql); await tx.CommitAsync();
    }

    internal LearningDbContext OpenRuntime() => new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(runtimeConnection).Options);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        Client?.Dispose(); if (app is not null) await app.DisposeAsync();
        try
        {
            if (runtimeRole is not null)
            {
                await using var db = LearningPostgresFixture.Open(Fixture); await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"REVOKE UPDATE(text) ON andril_learning.learning_personal_goals FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {runtimeRole}; DROP ROLE {runtimeRole}", (NpgsqlConnection)db.Database.GetDbConnection());
                await sql.ExecuteNonQueryAsync();
            }
        }
        finally { runtimeConnection = null; await Fixture.DisposeAsync(); }
    }
}
