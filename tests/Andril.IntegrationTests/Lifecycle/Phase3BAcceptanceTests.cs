using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.StarterModules;
using Andril.IntegrationTests.Database;
using Andril.Persistence.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

public sealed class Phase3BAcceptanceTests
{
    private static ModuleLifecycleCommand Command(string module = "learning", Guid? key = null, long? revision = null) =>
        new(module, key ?? Guid.NewGuid(), module == "social", revision);
    private static Task<ModuleLifecycleResult> Execute(ControlledLifecycleDatabase db, ModuleLifecycleCommand? command = null,
        IModuleLifecycleStore? store = null, CancellationToken ct = default) => db.Service(store).ExecuteAsync(db.Subject, command ?? Command(), Guid.NewGuid(), ct);
    private static Task<HttpResponseMessage> Post(ControlledLifecycleHost host, string module = "learning", string operation = "activation",
        object? body = null, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/account/modules/{module}/{operation}")
        { Content = JsonContent.Create(body ?? new { socialOptIn = module == "social" }) };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return host.Client.SendAsync(request);
    }

    [LocalPostgresFact, Trait("M6", "P3B-QA-01")]
    public async Task QA01_UnauthenticatedAndExpiredBearerDenied()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(host)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(db.Subject, expired: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(host)).StatusCode);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-02")]
    public async Task QA02_ForeignAccountInjectionRejectedWithoutAnyWrite()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var foreign = await SeedForeign(db);
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, body: new { accountId = foreign, socialOptIn = false })).StatusCode);
        host.Authenticate(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(host)).StatusCode);
        Assert.Equal(1, await db.Count("account_module_lifecycles"));
        Assert.Equal(0, await db.Count("account_module_lifecycle_audit"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-03")]
    public async Task QA03_InvalidModuleAndIdempotencyHeadersRejected()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        foreach (var module in new[] { "Learning", "chat", "socials" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, module)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/v1/account/modules/learning/activation", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, key: Guid.Empty)).StatusCode);
        using var text = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/modules/learning/activation") { Content = new StringContent("not-json") };
        text.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(text)).StatusCode);
        Assert.Equal(0, await db.Count("account_module_lifecycle_receipts"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-04")]
    public async Task QA04_UnderageSocialDeniedByStoredDemographics()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync(age: 16);
        db.Authority.SocialOptIn = true;
        var error = await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Command("social")));
        Assert.Equal(ModuleLifecycleDenial.IneligibleAge, error.Denial);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-05")]
    public async Task QA05_MissingCurrentSocialOptInAndMissingExplicitIntentDenied()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var error = await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Command("social")));
        Assert.Equal(ModuleLifecycleDenial.InvalidOptIn, error.Denial);
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(host, "social", body: new { })).StatusCode);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-06")]
    public async Task QA06_MissingAndSupersededConsentCannotAuthorize()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync(consent: false);
        var error = await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db));
        Assert.Equal(ModuleLifecycleDenial.MissingCurrentPolicies, error.Denial);
        await db.Sql($"INSERT INTO andril_identity.account_policy_records VALUES ('{db.AccountId}','terms_agreed','old-test-version', statement_timestamp()), ('{db.AccountId}','privacy_acknowledged','old-test-version', statement_timestamp())");
        Assert.Equal(ModuleLifecycleDenial.MissingCurrentPolicies,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db))).Denial);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-07")]
    public async Task QA07_UnavailableAndStaleAuthorityFailClosed()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        db.Authority.Outcome = ModuleAuthorizationOutcome.Unavailable;
        Assert.Equal(ModuleLifecycleDenial.EvidenceUnavailable,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db))).Denial);
        db.Authority.Outcome = ModuleAuthorizationOutcome.Satisfied;
        db.Authority.CurrentVersion = Guid.NewGuid();
        Assert.Equal(ModuleLifecycleDenial.StaleEvidence,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db))).Denial);
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        db.Authority.ThrowUnavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(host)).StatusCode);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-08")]
    public async Task QA08_ConcurrentFirstActivationHasOneTransitionAndDurableReplay()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var command = Command();
        // Independent pools/connections are necessary to exercise the database lock,
        // rather than serialization by the one-connection pooling test configuration.
        var settings = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = 16 };
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Execute(db, command,
            new ControlledModuleLifecycleStore(settings.ConnectionString, db.Authority, db.Authority, db.Authority, true))));
        Assert.All(results, x => Assert.Equal(results[0], x));
        Assert.Equal(1, await db.Count("account_module_lifecycles"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_audit"));
        Assert.Equal(results[0], await Execute(db, command));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-09")]
    public async Task QA09_RevisionConflictsPreserveFirstActivationAndSingleAudit()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var first = await Execute(db);
        await db.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=2 WHERE account_id='{db.AccountId}'");
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Command(revision: 1)));
        var parallel = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = 4 };
        IModuleLifecycleStore ParallelStore() => new ControlledModuleLifecycleStore(parallel.ConnectionString, db.Authority, db.Authority, db.Authority, true);
        var winner = Execute(db, Command(revision: 2), ParallelStore());
        var conflict = Execute(db, Command(revision: 3), ParallelStore());
        var result = await winner;
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => conflict);
        Assert.Equal(3, result.Revision);
        await using var check = new NpgsqlConnection(db.RuntimeConnection);
        await check.OpenAsync();
        await using var tx = await check.BeginTransactionAsync();
        await Scope(check, db);
        await using var query = new NpgsqlCommand("SELECT first_activated_at_utc FROM andril_identity.account_module_lifecycles", check);
        Assert.Equal(first.StateChangedAtUtc.UtcDateTime, (DateTime)(await query.ExecuteScalarAsync())!);
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-10")]
    public async Task QA10_ConflictingIdempotencyIntentRejectedByDatabaseAndHttp()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var command = Command();
        await Execute(db, command);
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, command with { ExpectedRevision = 1 }));
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(host, operation: "reactivation", body: new { expectedRevision = 1 }, key: command.RequestKey)).StatusCode);
        Assert.Equal(1, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_audit"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-11")]
    public async Task QA11_AuditFailureRollsBackLifecycleAndReceipt()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await db.Sql("ALTER TABLE andril_identity.account_module_lifecycle_audit ADD CONSTRAINT injected_audit_failure CHECK(false) NOT VALID");
        await Assert.ThrowsAsync<PostgresException>(() => Execute(db));
        foreach (var table in Tables) Assert.Equal(0, await db.Count(table));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-12")]
    public async Task QA12_LostCommitAcknowledgementReconcilesDurableReceipt()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var command = Command();
        var fault = new CommitFault(db.Store(), afterCommit: true);
        var result = await Execute(db, command, fault);
        Assert.Equal(1, fault.Writes);
        Assert.Equal(1, fault.Reads);
        Assert.Equal(result, await Execute(db, command));
        foreach (var table in Tables) Assert.Equal(1, await db.Count(table));
        var unresolved = new CommitFault(db.Store(), afterCommit: false);
        await Assert.ThrowsAsync<ModuleLifecycleCommitUnknownException>(() => Execute(db, Command(key: Guid.NewGuid()), unresolved));
        Assert.Equal(1, unresolved.Writes);
        await using var host = await ControlledLifecycleHost.StartAsync(db, decorate: store => new CommitFault(store, afterCommit: false));
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(host)).StatusCode);
        Assert.Equal(1, await db.Count("account_module_lifecycle_audit"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-13")]
    public async Task QA13_LearningCannotReadOrWriteOtherModules()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db);
        await using var connection = new NpgsqlConnection(db.RuntimeConnection);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Scope(connection, db);
        await using var sql = new NpgsqlCommand("INSERT INTO andril_identity.account_module_lifecycles(account_id,module,state,revision) VALUES (@account,'hobbies','active',1)", connection);
        sql.Parameters.AddWithValue("account", db.AccountId);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(1, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-14")]
    public async Task QA14_RevokedAuthorityDeniesReactivationAndReplay()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var command = Command();
        await Execute(db, command);
        db.Authority.Restrictions = false;
        Assert.Equal(ModuleLifecycleDenial.AccountRestricted,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, command))).Denial);
        await db.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=2 WHERE account_id='{db.AccountId}'");
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Command(revision: 2)));
        Assert.Equal(1, await db.Count("account_module_lifecycle_audit"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-15")]
    public async Task QA15_ActiveLifecycleDoesNotAuthorizeHome()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.OK, (await Post(host)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/v1/modules/learning/home")).StatusCode);
        Assert.False(new Andril.Common.Accounts.Onboarding.OnboardingStatus("done", new(true,true,true,true,true), "learning").ModuleHomeAuthorized);
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-16")]
    public async Task QA16_RestrictedRuntimeCannotInjectForeignAccount()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var foreign = await SeedForeign(db);
        await Execute(db);
        await using var connection = new NpgsqlConnection(db.RuntimeConnection);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await Scope(connection, db, account: foreign);
        await using var sql = new NpgsqlCommand("SELECT count(*) FROM andril_identity.account_module_lifecycles", connection);
        Assert.Equal(0L, await sql.ExecuteScalarAsync());
        foreach (var table in Tables)
        {
            sql.CommandText = $"SELECT count(*) FROM andril_identity.{table}";
            Assert.Equal(0L, await sql.ExecuteScalarAsync());
        }
        // Even substituting every GUC cannot escape the disposable role's fixed
        // account binding. GUCs alone are not an authentication mechanism.
        await using (var owner = db.Open())
        {
            var foreignSubject = await owner.Accounts.Where(x => x.Id == foreign).Select(x => x.SupabaseUserId).SingleAsync();
            sql.CommandText = "SELECT set_config('andril.subject',@subject,true)";
            sql.Parameters.AddWithValue("subject", foreignSubject.ToString());
            await sql.ExecuteNonQueryAsync();
            sql.Parameters.Clear();
            sql.CommandText = "SELECT count(*) FROM andril_identity.account_module_lifecycles";
            Assert.Equal(0L, await sql.ExecuteScalarAsync());
        }
        sql.CommandText = "UPDATE andril_identity.account_module_lifecycles SET revision=999";
        Assert.Equal(0, await sql.ExecuteNonQueryAsync());
        sql.CommandText = "INSERT INTO andril_identity.account_module_lifecycles(account_id,module,state,revision) VALUES (@account,'learning','active',1)";
        sql.Parameters.AddWithValue("account", foreign);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(2, await db.Count("account_module_lifecycles"));
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-17")]
    public async Task QA17_SingleConnectionPoolClearsCommittedAndRolledBackIdentity()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db);
        foreach (var commit in new[] { true, false })
        {
            await using (var first = new NpgsqlConnection(db.RuntimeConnection))
            {
                await first.OpenAsync();
                await using var tx = await first.BeginTransactionAsync();
                await Scope(first, db);
                if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
            }
            await using var next = new NpgsqlConnection(db.RuntimeConnection);
            await next.OpenAsync();
            await using var sql = new NpgsqlCommand("SELECT count(*) FROM andril_identity.account_module_lifecycles", next);
            Assert.Equal(0L, await sql.ExecuteScalarAsync());
            sql.CommandText = "SELECT nullif(current_setting('andril.subject',true),'') IS NULL AND nullif(current_setting('andril.account',true),'') IS NULL AND nullif(current_setting('andril.module',true),'') IS NULL";
            Assert.Equal(true, await sql.ExecuteScalarAsync());
        }
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-18")]
    public async Task QA18_RuntimeCannotDdlMutateJournalsOrAccessHistory()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(db.RuntimeConnection);
        await connection.OpenAsync();
        foreach (var statement in new[] { "CREATE TABLE public.forbidden(id integer)", "CREATE SCHEMA forbidden",
            "ALTER TABLE andril_identity.account_module_lifecycles DISABLE ROW LEVEL SECURITY",
            "SELECT * FROM andril_identity.\"__EFMigrationsHistory\"", "DELETE FROM andril_identity.account_module_lifecycle_receipts",
            "UPDATE andril_identity.account_module_lifecycle_audit SET revision=999",
            "UPDATE andril_identity.account_module_lifecycles SET first_activated_at_utc=clock_timestamp()" })
        {
            await using var sql = new NpgsqlCommand(statement, connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync())).SqlState);
        }
        await using var flags = new NpgsqlCommand("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname=current_user", connection);
        Assert.Equal(false, await flags.ExecuteScalarAsync());
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-19")]
    public async Task QA19_LegacyStarterDoesNotBackfillOrAuthorizeHome()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using (var context = db.Open())
        {
            context.StarterMemberships.Add(StarterMembership.Create(db.AccountId, "learning", false, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/v1/modules/learning/home")).StatusCode);
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
        Assert.Null((await db.Store().ReadAsync(db.Subject, "learning", Guid.NewGuid(), default)).Current);
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-20")]
    public async Task QA20_CancellationAndInfrastructureFailureCannotSucceed()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(db, ct: cancellation.Token));
        await using (var blocker = new NpgsqlConnection(db.MigrationConnection))
        {
            await blocker.OpenAsync();
            await using var lockTx = await blocker.BeginTransactionAsync();
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@subject,0))", blocker);
            hold.Parameters.AddWithValue("subject", db.Subject.ToString());
            await hold.ExecuteNonQueryAsync();
            using var inFlight = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(db, ct: inFlight.Token));
            await lockTx.RollbackAsync();
        }
        db.Authority.ThrowUnavailable = true;
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(host)).StatusCode);
        foreach (var table in Tables) Assert.Equal(0, await db.Count(table));
        db.Authority.ThrowUnavailable = false;
        Assert.Equal(HttpStatusCode.OK, (await Post(host)).StatusCode);
    }
    [LocalPostgresFact, Trait("M6", "P3B-QA-21")]
    public async Task QA21_MinimalResponseSafeLogsAndNoStoreIncludingErrors()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        var key = Guid.NewGuid();
        using var response = await Post(host, key: key);
        var json = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(new[] { "module", "revision", "state", "stateChangedAtUtc" }, parsed.RootElement.EnumerateObject().Select(x => x.Name).Order());
        Assert.True(response.Headers.CacheControl?.NoStore);
        db.Authority.ThrowUnavailable = true;
        using var failure = await Post(host);
        Assert.True(failure.Headers.CacheControl?.NoStore);
        var all = json + await failure.Content.ReadAsStringAsync() + string.Join('\n', host.SafeLog);
        foreach (var sensitive in new[] { db.AccountId.ToString(), db.Subject.ToString(), key.ToString(), "synthetic@example.invalid", "test-terms", "Password", "Synthetic dependency", "dateOfBirth" })
            Assert.DoesNotContain(sensitive, all, StringComparison.OrdinalIgnoreCase);
    }

    [LocalPostgresFact]
    public async Task OpenApiAndRateLimitingAreExercisedInOptedInHost()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ControlledLifecycleHost.StartAsync(db, optIn: false));
        await using var host = await ControlledLifecycleHost.StartAsync(db, limit: 1);
        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/openapi/v1.json"));
        foreach (var route in new[] { "activation", "reactivation" })
        {
            var post = document.RootElement.GetProperty("paths").GetProperty("/api/v1/account/modules/{module}/" + route).GetProperty("post");
            foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "429", "503" })
                Assert.True(post.GetProperty("responses").TryGetProperty(status, out _));
            var header = Assert.Single(post.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.True(header.GetProperty("required").GetBoolean());
            Assert.Equal("uuid", header.GetProperty("schema").GetProperty("format").GetString());
        }
        Assert.Equal("bearer", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("testBearer").GetProperty("scheme").GetString());
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.OK, (await Post(host)).StatusCode);
        using var limited = await Post(host);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.CacheControl?.NoStore);
    }

    [LocalPostgresFact]
    public async Task MigrationDowngradeAndUpgradePreserveExistingAccountsAndUnrelatedTables()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var context = db.Open();
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE public.lifecycle_sentinel(id integer PRIMARY KEY); INSERT INTO public.lifecycle_sentinel VALUES(1)");
        await context.GetService<IMigrator>().MigrateAsync("20261120152336_AddPrivateProfilePhotos");
        Assert.Equal(1, await context.Accounts.CountAsync());
        await context.Database.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM public.lifecycle_sentinel").SingleAsync());
        foreach (var table in Tables) Assert.Equal(0, await db.Count(table));
    }

    [LocalPostgresFact]
    public async Task HttpReactivationReturnsOnlyStateAndUnavailableAuthorityNeverFallsBack()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var unavailable = new ControlledModuleLifecycleStore(db.RuntimeConnection, db.Authority, db.Authority, null, true);
        Assert.Equal(ModuleLifecycleDenial.EvidenceUnavailable,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, store: unavailable))).Denial);
        await using var host = await ControlledLifecycleHost.StartAsync(db);
        host.Authenticate(db.Subject);
        Assert.Equal(HttpStatusCode.OK, (await Post(host)).StatusCode);
        await db.Sql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=2 WHERE account_id='{db.AccountId}'");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, operation: "reactivation", body: new { expectedRevision = 0 })).StatusCode);
        using var result = await Post(host, operation: "reactivation", body: new { expectedRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/v1/modules/learning/home")).StatusCode);
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    private static readonly string[] Tables = ["account_module_lifecycles", "account_module_lifecycle_receipts", "account_module_lifecycle_audit"];
    private static async Task<Guid> SeedForeign(ControlledLifecycleDatabase db)
    {
        await using var context = db.Open();
        var foreign = Andril.Common.Accounts.Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        context.Accounts.Add(foreign);
        await context.SaveChangesAsync();
        await db.Sql($"INSERT INTO andril_identity.account_module_lifecycles(account_id,module,state,revision) VALUES ('{foreign.Id}','learning','active',1)");
        return foreign.Id;
    }
    private static async Task Scope(NpgsqlConnection connection, ControlledLifecycleDatabase db, Guid? account = null)
    {
        await using var sql = new NpgsqlCommand("SELECT set_config('andril.subject',@subject,true),set_config('andril.account',@account,true),set_config('andril.module','learning',true)", connection);
        sql.Parameters.AddWithValue("subject", db.Subject.ToString());
        sql.Parameters.AddWithValue("account", (account ?? db.AccountId).ToString());
        await sql.ExecuteNonQueryAsync();
    }
    private sealed class CommitFault(IModuleLifecycleStore inner, bool afterCommit) : IModuleLifecycleStore
    {
        internal int Writes;
        internal int Reads;
        public async Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid key, Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken ct)
        {
            Writes++;
            if (afterCommit) await inner.WriteAsync(subject, module, key, decide, ct);
            throw new ModuleLifecycleCommitUnknownException();
        }
        public Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid key, CancellationToken ct)
        { Reads++; return inner.ReadAsync(subject, module, key, ct); }
    }
}
