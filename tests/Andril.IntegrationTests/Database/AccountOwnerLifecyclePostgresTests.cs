using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Andril.API.Authentication;
using Andril.API.Extensions;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Persistence.Accounts;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AccountOwnerLifecyclePostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [LocalPostgresFact]
    public async Task OwnerDeactivationAndReactivationPersistWithoutChangingAccountIdentity()
    {
        await using var f = await Fixture.Create();
        var original = f.Account;
        var inactive = await f.Run(Command(AccountOwnerOperation.Deactivate));
        Assert.Equal(AccountLifecycleState.Deactivated, inactive.State);
        var active = await f.Run(Command(AccountOwnerOperation.Reactivate, 2));
        Assert.Equal(AccountLifecycleState.Active, active.State);
        Assert.Equal(3, active.Revision);
        await using var db = f.Database.Open();
        var account = await db.Accounts.SingleAsync();
        Assert.Equal(original.Id, account.Id);
        Assert.Equal(original.SupabaseUserId, account.SupabaseUserId);
        Assert.Equal(original.CreatedAtUtc, account.CreatedAtUtc);
        Assert.Equal(original.UpdatedAtUtc, account.UpdatedAtUtc);
        Assert.Equal((2, 2), await f.JournalCounts());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [LocalPostgresFact]
    public async Task TwelveConcurrentIdenticalDeletionsCommitOneReceiptAndAudit()
    {
        await using var f = await Fixture.Create();
        var command = Command(AccountOwnerOperation.Delete, confirm: true);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Run(command)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(AccountLifecycleState.Deleted, results[0].State);
        Assert.Equal((1, 1), await f.JournalCounts());
        await using (var db = f.Database.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await f.Bind(db);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE andril_identity.account_owner_states SET state='Active',revision=revision+1,changed_at_utc=clock_timestamp() WHERE account_id={f.Account.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        }
        var error = await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Run(Command(AccountOwnerOperation.Reactivate, 2)));
        Assert.Equal(AccountOwnerLifecycleDenial.Denied, error.Denial);
    }

    [LocalPostgresFact]
    public async Task ConcurrentDifferentCommandsCannotConsumeTheSameRevision()
    {
        await using var f = await Fixture.Create();
        var tasks = new[] { Command(AccountOwnerOperation.Deactivate), Command(AccountOwnerOperation.Delete, confirm: true) }
            .Select(async command =>
            {
                try { await f.Run(command); return true; }
                catch (AccountOwnerLifecycleException) { return false; }
            });
        Assert.Single(await Task.WhenAll(tasks), success => success);
        Assert.Equal((1, 1), await f.JournalCounts());
    }

    [LocalPostgresFact]
    public async Task MissingAndChangedAuthorityCannotInitializeActiveOrCommitWrites()
    {
        await using var f = await Fixture.Create();
        f.Authority.Outcome = AccountOwnerAuthorityOutcome.Unavailable;
        await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Run(Command(AccountOwnerOperation.Deactivate)));
        Assert.Equal((0, 0), await f.JournalCounts());
        f.Authority.Outcome = AccountOwnerAuthorityOutcome.Satisfied;
        f.Authority.ChangeOnSecondRead = true;
        await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Run(Command(AccountOwnerOperation.Deactivate)));
        Assert.Equal((0, 0), await f.JournalCounts());
        await using var db = f.Database.Open();
        await using var tx = await db.Database.BeginTransactionAsync();
        await f.Bind(db);
        Assert.Empty(await db.Set<AccountOwnerStateRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task AuditConstraintFailureRollsBackStateAndReceiptTogether()
    {
        await using var f = await Fixture.Create();
        await using (var setup = f.Database.Open())
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE andril_identity.account_owner_audit ADD CONSTRAINT synthetic_failure CHECK (false)");
        await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Run(Command(AccountOwnerOperation.Delete, confirm: true)));
        Assert.Equal((0, 0), await f.JournalCounts());
        await using var db = f.Database.Open();
        await using var tx = await db.Database.BeginTransactionAsync();
        await f.Bind(db);
        Assert.Empty(await db.Set<AccountOwnerStateRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task SuspendedAndDeletedStatesCannotBeSelfRestoredAndDowngradePreservesAudit()
    {
        await using var f = await Fixture.Create();
        await using (var db = f.Database.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await f.Bind(db);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_identity.account_owner_states (account_id,state,revision,changed_at_utc) VALUES ({f.Account.Id},'Suspended',1,{Now})");
            await tx.CommitAsync();
        }
        foreach (var op in Enum.GetValues<AccountOwnerOperation>())
            await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Run(Command(op, confirm: op == AccountOwnerOperation.Delete)));
        await using (var invalid = f.Database.Open())
        {
            await using var tx = await invalid.Database.BeginTransactionAsync();
            await f.Bind(invalid);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => invalid.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE andril_identity.account_owner_states SET state='Restricted',revision=revision+1,changed_at_utc=clock_timestamp() WHERE account_id={f.Account.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        }
        await using var migrate = f.Database.Open();
        await Assert.ThrowsAsync<PostgresException>(() => migrate.GetService<IMigrator>().MigrateAsync("20261128094614_BindProfileMetadataOwnership"));
        await using var verify = f.Database.Open();
        Assert.Contains("20261128145313_AddAccountOwnerLifecycle", await verify.Database.GetAppliedMigrationsAsync());
        Assert.True(await verify.Database.SqlQuery<bool>($"SELECT relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_identity.account_owner_states'::regclass").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task RestrictedRoleCannotCrossAccountScopeMutateAuditOrReadAfterPoolContextReset()
    {
        await using var f = await Fixture.Create();
        await f.Run(Command(AccountOwnerOperation.Deactivate));
        var role = "owner_runtime_" + Guid.NewGuid().ToString("N");
        using var quote = new NpgsqlCommandBuilder();
        var quoted = quote.QuoteIdentifier(role);
        try
        {
            await using (var setup = f.Database.Open())
                await RoleSql(setup, $"CREATE ROLE {quoted} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS; GRANT {quoted} TO CURRENT_USER; GRANT USAGE ON SCHEMA andril_identity TO {quoted}; GRANT SELECT ON andril_identity.accounts, andril_identity.account_owner_states, andril_identity.account_owner_receipts TO {quoted}; GRANT INSERT ON andril_identity.account_owner_audit TO {quoted}; CREATE POLICY {quoted} ON andril_identity.accounts FOR SELECT TO {quoted} USING (id = '{f.Account.Id}'::uuid AND supabase_user_id = '{f.User.UserId}'::uuid AND supabase_user_id = nullif(current_setting('andril.subject',true),'')::uuid)");
            await using (var db = f.Database.Open())
            {
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    await f.Bind(db);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{role},true)");
                    Assert.Single(await db.Set<AccountOwnerStateRow>().ToListAsync());
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Guid.NewGuid().ToString()},true)");
                    Assert.Empty(await db.Set<AccountOwnerStateRow>().ToListAsync());
                    await tx.CommitAsync();
                }
                await using var second = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{role},true)");
                Assert.Empty(await db.Set<AccountOwnerStateRow>().ToListAsync());
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM andril_identity.account_owner_audit"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
        finally
        {
            await using var cleanup = f.Database.Open();
            await RoleSql(cleanup, $"DROP POLICY IF EXISTS {quoted} ON andril_identity.accounts; DROP OWNED BY {quoted}; DROP ROLE {quoted}");
        }
    }

    [LocalPostgresFact]
    public async Task SignedOwnerHttpRejectsForeignInjectionAndOldTokenProtectedAccessAfterDeletion()
    {
        await using var f = await Fixture.Create();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = "owner-test", ValidateAudience = true,
                ValidAudience = "owner-test", ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidateLifetime = true, ClockSkew = TimeSpan.Zero };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddScoped<IOnboardingIdentity, SyntheticConfirmedHttpIdentity>();
        using var prototype = f.Database.Open();
        var connection = prototype.Database.GetConnectionString();
        builder.Services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql(connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")));
        builder.Services.AddAccountOwnerLifecycle(_ => f.Authority);
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.Response.Headers.CacheControl = "no-store"; await next(); });
        app.UseAuthentication(); app.UseAuthorization();
        app.MapAccountOwnerLifecycle();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization().AddEndpointFilter<ActiveAccountOwnerFilter>();
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/account/lifecycle")).StatusCode);
        var token = new JwtSecurityToken("owner-test", "owner-test", [new("sub", f.User.UserId!.Value.ToString()),
            new("role", "authenticated"), new("is_anonymous", "false")], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/account/lifecycle/delete", new { requestKey = Guid.NewGuid(), expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/account/lifecycle/deactivate", new { requestKey = Guid.NewGuid(), expectedRevision = 1, accountId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/account/lifecycle/deactivate", new { requestKey = Guid.NewGuid(), expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/account/lifecycle/reactivate", new { requestKey = Guid.NewGuid(), expectedRevision = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/protected")).StatusCode);
        using var deleted = await client.PostAsJsonAsync("/api/v1/account/lifecycle/delete", new { requestKey = Guid.NewGuid(), expectedRevision = 3, confirmDeletion = true });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await deleted.Content.ReadAsStringAsync();
        Assert.DoesNotContain(f.Account.Id.ToString(), body);
        Assert.Contains("deleted", body);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/account/lifecycle/reactivate", new { requestKey = Guid.NewGuid(), expectedRevision = 4 })).StatusCode);
    }

    private static AccountOwnerLifecycleCommand Command(AccountOwnerOperation op, long revision = 1, bool confirm = false) => new(op, Guid.NewGuid(), revision, confirm);
    private static async Task RoleSql(AccountsDbContext db, string sql)
    {
        // Identifiers above come only from our generated GUID and QuoteIdentifier.
        // PostgreSQL identifiers cannot be bound as SQL value parameters.
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private sealed class User(Guid subject) : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid value) => value == subject;
    }
    private sealed class SyntheticConfirmedHttpIdentity(ICurrentUser user) : IOnboardingIdentity
    { public bool IsConfirmedFor(Guid subject) => user.IsAuthenticated && user.UserId == subject; }
    private sealed class Authority : IAccountOwnerLifecycleAuthority
    {
        internal AccountOwnerAuthorityOutcome Outcome { get; set; } = AccountOwnerAuthorityOutcome.Satisfied;
        internal bool ChangeOnSecondRead { get; set; }
        private Guid version = Guid.NewGuid(); private int reads;
        public Task<AccountOwnerAuthority> ReadAsync(Guid subject, Guid account, AccountOwnerOperation op, CancellationToken ct)
        {
            if (ChangeOnSecondRead && Interlocked.Increment(ref reads) == 2) version = Guid.NewGuid();
            return Task.FromResult(new AccountOwnerAuthority(Outcome, version, version, true));
        }
    }
    private sealed class Fixture(LocalAccountDatabase database, Account account, User user) : IAsyncDisposable
    {
        internal LocalAccountDatabase Database => database;
        internal Account Account => account;
        internal User User => user;
        internal Authority Authority { get; } = new();
        internal static async Task<Fixture> Create()
        {
            var database = await LocalAccountDatabase.CreateAsync();
            try
            {
                var user = new User(Guid.NewGuid());
                var account = Account.Create(user.UserId!.Value, Now);
                await using var db = database.Open(); db.Accounts.Add(account); await db.SaveChangesAsync();
                return new(database, account, user);
            }
            catch { await database.DisposeAsync(); throw; }
        }
        internal async Task<AccountOwnerLifecycleResult> Run(AccountOwnerLifecycleCommand command)
        {
            await using var db = database.Open();
            return await new AccountOwnerLifecycleService(user, user, new PostgresAccountOwnerLifecycleStore(db, user, Authority)).ExecuteAsync(command);
        }
        internal Task Bind(AccountsDbContext db) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{user.UserId.ToString()},true),set_config('andril.account',{account.Id.ToString()},true)");
        internal async Task<(int, int)> JournalCounts()
        {
            await using var db = database.Open(); await using var tx = await db.Database.BeginTransactionAsync(); await Bind(db);
            return (await db.Set<AccountOwnerReceiptRow>().CountAsync(), await db.Set<AccountOwnerAuditRow>().CountAsync());
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
