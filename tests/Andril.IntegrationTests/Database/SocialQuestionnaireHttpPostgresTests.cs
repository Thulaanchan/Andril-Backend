using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Authentication;
using Andril.Persistence.DbContexts;
using Andril.Social.Personality;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialQuestionnaireHttpPostgresTests
{
    private const string Active = "/api/v1/social/questionnaires/active";
    private const string Responses = "/api/v1/social/questionnaires/responses";
    private const string Mine = Responses + "/me";
    private const string Terms = "fixture-terms-v1";
    private const string Privacy = "fixture-privacy-v1";
    private const string Scheme = "QuestionnaireFixture";
    private static readonly QuestionnaireDefinition Definition = QuestionnaireV1Definition.Create();

    [LocalPostgresFact]
    public async Task EligibleCallerReadsDefinitionSubmitsCompletedAndRetrievesThroughAnotherRequest()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        using var host = new Host(store);
        using var client = Client(host, owner);

        var definition = await JsonAsync(await client.GetAsync(Active), HttpStatusCode.OK);
        Assert.Equal(Definition.Version, definition.GetProperty("version").GetString());
        var empty = await client.GetAsync(Mine);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        NoStore(empty);

        var posted = await JsonAsync(await client.PostAsJsonAsync(Responses, Submission(skip: false)), HttpStatusCode.OK);
        Assert.Equal("Completed", posted.GetProperty("status").GetString());
        Assert.Equal(new[] { "responseId", "status" }, posted.EnumerateObject().Select(p => p.Name).Order());
        using var anotherRequestScope = Client(host, owner);
        var current = await JsonAsync(await anotherRequestScope.GetAsync(Mine), HttpStatusCode.OK);
        Assert.Equal(posted.GetProperty("responseId").GetGuid(), current.GetProperty("responseId").GetGuid());
        Assert.Equal("Completed", current.GetProperty("status").GetString());
        Assert.Equal(Definition.Questions.Count, current.GetProperty("answers").GetArrayLength());
        Assert.Equal(new[] { "answers", "createdAtUtc", "responseId", "status", "version" },
            current.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(owner.Account.ToString(), current.GetRawText());
        Assert.DoesNotContain(owner.Profile.ToString(), current.GetRawText());
        Assert.Equal((1, Definition.Questions.Count, 1L), await store.CountsAsync());
        Assert.Equal(owner.Profile, (await store.ReadAsync(owner))?.ProfileId);
    }

    [LocalPostgresFact]
    public async Task SkippedSubmissionBecomesCurrentWithNoPersistedAnswers()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        using var host = new Host(store);
        using var client = Client(host, owner);
        var posted = await JsonAsync(await client.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.OK);
        var read = await JsonAsync(await client.GetAsync(Mine), HttpStatusCode.OK);
        Assert.Equal(posted.GetProperty("responseId").GetGuid(), read.GetProperty("responseId").GetGuid());
        Assert.Equal("Skipped", read.GetProperty("status").GetString());
        Assert.Empty(read.GetProperty("answers").EnumerateArray());
        Assert.Equal((1, 0, 1L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task MissingAndUnconfirmedAuthenticationAreDeniedWithNoStore()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var caller = await SeedAsync(store);
        using var host = new Host(store);
        using var anonymous = Client(host);
        using var unconfirmed = Client(host, caller, confirmed: false);
        foreach (var path in new[] { Active, Mine })
        {
            await JsonStatusAsync(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
            await JsonStatusAsync(await unconfirmed.GetAsync(path), HttpStatusCode.Forbidden);
        }
        await JsonStatusAsync(await anonymous.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.Unauthorized);
        await JsonStatusAsync(await unconfirmed.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.Forbidden);
        Assert.Equal((0, 0, 0L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task UnderageNonOptedInInactiveAndCrossModuleProfilesCannotSubmitOrRetrieve()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var underage = await SeedAsync(store, minor: true);
        var noOptIn = await SeedAsync(store, optedIn: false);
        var inactive = await SeedAsync(store, active: false);
        var hobbies = await SeedAsync(store, module: "hobbies");
        using var host = new Host(store);
        foreach (var actor in new[] { underage, noOptIn, inactive, hobbies })
        {
            using var client = Client(host, actor);
            await JsonStatusAsync(await client.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.Forbidden);
            await JsonStatusAsync(await client.GetAsync(Mine), HttpStatusCode.Forbidden);
        }
        Assert.Equal((0, 0, 0L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task ForeignProfileCannotBeSelectedAndInvalidSubmissionsAreNotPersisted()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        var other = await SeedAsync(store);
        using var host = new Host(store);
        using var client = Client(host, owner);
        using var foreign = Client(host, other);
        await JsonAsync(await client.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.OK);
        await JsonStatusAsync(await foreign.GetAsync(Mine + "?profileId=" + owner.Profile), HttpStatusCode.NotFound);

        var inputs = new object[]
        {
            new { version = "fixture-unknown", skip = true, answers = Array.Empty<object>() },
            new { version = Definition.Version, skip = false, answers = new[] { new { questionId = "invalid", optionId = "3" } } },
            new { version = Definition.Version, skip = true, answers = Array.Empty<object>(), profileId = owner.Profile }
        };
        foreach (var input in inputs)
        {
            var bad = await JsonAsync(await foreign.PostAsJsonAsync(Responses, input), HttpStatusCode.BadRequest);
            Assert.DoesNotContain(owner.Profile.ToString(), bad.GetRawText());
        }
        Assert.Equal((1, 0, 1L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task ConcurrentAndRepeatedSubmissionsHaveOneCommitAndSafeConflict()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        using var host = new Host(store);
        using var first = Client(host, owner);
        using var second = Client(host, owner);
        var results = await Task.WhenAll(first.PostAsJsonAsync(Responses, Submission()), second.PostAsJsonAsync(Responses, Submission()));
        await JsonAsync(Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK);
        var conflict = Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        await VerifyConflict(conflict);
        await VerifyConflict(await first.PostAsJsonAsync(Responses, Submission()));
        Assert.Equal((1, 0, 1L), await store.CountsAsync());

        async Task VerifyConflict(HttpResponseMessage response)
        {
            var problem = await JsonAsync(response, HttpStatusCode.Conflict);
            Assert.Equal("CURRENT_RESPONSE_EXISTS", problem.GetProperty("code").GetString());
            Assert.DoesNotContain(owner.Profile.ToString(), problem.GetRawText());
            Assert.False(problem.TryGetProperty("responseId", out _));
        }
    }

    [LocalPostgresFact]
    public async Task ActualDatabaseOutageReturnsSafeUnavailableAndNoNewRows()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        using var host = new Host(store);
        using var client = Client(host, owner);
        await JsonStatusAsync(await client.GetAsync(Mine), HttpStatusCode.NotFound);

        await using var admin = await SocialLifecycleDatabase.OpenMaintenanceAsync();
        using var identifiers = new NpgsqlCommandBuilder();
        var name = identifiers.QuoteIdentifier(store.Database.DatabaseName);
        await using var change = new NpgsqlCommand($"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS false", admin);
        await change.ExecuteNonQueryAsync();
        try
        {
            var unavailable = await JsonAsync(await client.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.ServiceUnavailable);
            Assert.DoesNotContain(store.Database.DatabaseName, unavailable.GetRawText());
            Assert.DoesNotContain("Npgsql", unavailable.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            change.CommandText = $"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS true";
            await change.ExecuteNonQueryAsync();
        }
        Assert.Equal((0, 0, 0L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task RequestCancellationDuringWriteLeavesNoPersistedResponse()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        var pause = new PauseWrite();
        using var host = new Host(store, pause: pause);
        using var client = Client(host, owner);
        using var cancelled = new CancellationTokenSource();
        var request = client.PostAsJsonAsync(Responses, Submission(skip: false), cancelled.Token);
        try
        {
            var signaled = await Task.WhenAny(request, pause.Entered.Task).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Same(pause.Entered.Task, signaled);
        }
        finally { await cancelled.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await pause.Exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((0, 0, 0L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task DisabledActivationLeavesResponseRoutesUnmappedAndPreservesDefinition()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var owner = await SeedAsync(store);
        using var host = new Host(store, enabled: false);
        using var client = Client(host, owner);
        await JsonStatusAsync(await client.GetAsync(Active), HttpStatusCode.OK);
        await JsonStatusAsync(await client.PostAsJsonAsync(Responses, Submission()), HttpStatusCode.NotFound);
        await JsonStatusAsync(await client.GetAsync(Mine), HttpStatusCode.NotFound);
        Assert.Equal((0, 0, 0L), await store.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task DevelopmentOpenApiListsOnlyTheSupportedQuestionnaireOperations()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        using var host = new Host(store);
        using var client = Client(host);
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = document.GetProperty("paths");
        Assert.Equal(new[] { Active, Responses, Mine }, paths.EnumerateObject().Select(p => p.Name)
            .Where(path => path.StartsWith("/api/v1/social/questionnaires/", StringComparison.Ordinal)).Order());
        Assert.True(paths.GetProperty(Active).TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(Mine).TryGetProperty("get", out _));
        var submit = paths.GetProperty(Responses).GetProperty("post");
        Assert.True(submit.GetProperty("responses").TryGetProperty("409", out _));
        Assert.False(paths.GetProperty(Responses).TryGetProperty("delete", out _));
    }

    private static object Submission(bool skip = true) => new
    {
        version = Definition.Version,
        skip,
        answers = skip ? [] : Definition.Questions.Select(question => new { questionId = question.Id, optionId = "3" }).ToArray()
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            await JsonStatusAsync(response, status);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }

    private static Task JsonStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        NoStore(response);
        return Task.CompletedTask;
    }

    private static void NoStore(HttpResponseMessage response) => Assert.True(response.Headers.CacheControl?.NoStore);

    private static HttpClient Client(Host host, SocialLifecycleDatabase.Actor? actor = null, bool confirmed = true)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (actor is not null) client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (confirmed ? "confirmed-" : "unconfirmed-") + actor.Subject);
        return client;
    }

    internal static async Task<SocialLifecycleDatabase.Actor> SeedAsync(SocialLifecycleDatabase store,
        bool active = true, bool optedIn = true, bool minor = false, string module = "social")
    {
        await using var accounts = store.Database.OpenAccounts();
        SocialLifecycleDatabase.Actor actor;
        if (optedIn) actor = await store.SeedAsync(module, active);
        else
        {
            actor = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await accounts.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                    VALUES ({actor.Account}, {actor.Subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                    VALUES ({actor.Profile}, {actor.Account}, 'social', CURRENT_TIMESTAMP);
                """);
        }
        var birth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(minor ? -16 : -28);
        await accounts.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_demographics(account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                VALUES ({actor.Account}, {birth}, 'fixture', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_policy_records(account_id, action, version, recorded_at_utc)
                VALUES ({actor.Account}, 'terms_agreed', {Terms}, CURRENT_TIMESTAMP),
                       ({actor.Account}, 'privacy_acknowledged', {Privacy}, CURRENT_TIMESTAMP);
            """);
        return actor;
    }

    private sealed class Host(SocialLifecycleDatabase store, bool enabled = true, PauseWrite? pause = null)
        : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var settings = new Dictionary<string, string?>
            {
                ["SocialQuestionnaire:EnablePersistence"] = enabled.ToString(),
                ["SocialQuestionnaire:RuntimeRole"] = store.Role,
                ["ConnectionStrings:AndrilAccounts"] = store.Database.ConnectionString
            };
            foreach (var (name, version) in new[] { ("Terms", Terms), ("Privacy", Privacy), ("CommunityGuidelines", "fixture-community-v1") })
            {
                settings[$"AccountPolicies:{name}:Version"] = version;
                settings[$"AccountPolicies:{name}:Language"] = "en";
                settings[$"AccountPolicies:{name}:Url"] = $"https://example.invalid/{name}";
            }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = Scheme;
                    options.DefaultChallengeScheme = Scheme;
                    options.DefaultForbidScheme = Scheme;
                }).AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>(Scheme, _ => { });
                services.RemoveAll<ISupabaseUserVerifier>();
                services.AddSingleton<ISupabaseUserVerifier, FixtureConfirmation>();
                services.AddSingleton<IStartupFilter, Loopback>();
                if (pause is not null) services.ConfigureDbContext<SocialDbContext>(options => options.AddInterceptors(pause));
            });
        }
    }

    internal sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var authorization = Request.Headers.Authorization.ToString();
            if (!AuthenticationHeaderValue.TryParse(authorization, out var header) || header.Scheme != "Bearer" ||
                header.Parameter is not { } token)
                return Task.FromResult(AuthenticateResult.NoResult());

            var separator = token.IndexOf('-');
            if (separator < 0 || !Guid.TryParse(token[(separator + 1)..], out var subject))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new("sub", subject.ToString()), new("role", "authenticated"), new("is_anonymous", "false")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    internal sealed class FixtureConfirmation : ISupabaseUserVerifier
    {
        public Task<SupabaseUserVerification> VerifyAsync(Guid subject, string accessToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(accessToken == "confirmed-" + subject ? SupabaseUserVerification.Confirmed
                : accessToken == "unconfirmed-" + subject ? SupabaseUserVerification.Unconfirmed : SupabaseUserVerification.Unauthorized);
        }
    }

    internal sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class PauseWrite : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO andril_social.social_questionnaire_responses", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Exited.TrySetResult(); }
            }
            return result;
        }
    }
}
