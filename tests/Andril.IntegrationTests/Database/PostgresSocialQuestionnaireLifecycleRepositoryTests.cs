using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PostgresSocialQuestionnaireLifecycleRepositoryTests
{
    [LocalPostgresFact]
    public async Task CompletedResponseAndAnswersPersistAcrossIndependentContexts()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var owner = await fixture.SeedAsync();
        var response = Response(owner.Profile);
        Assert.Null(await fixture.ReadAsync(owner));
        await fixture.SaveAsync(owner, response);

        var current = Assert.IsType<QuestionnaireResponseEntity>(await fixture.ReadAsync(owner));
        Assert.Equal(response.Id, current.Id);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, current.Status);
        Assert.Equal(response.Answers.Select(answer => answer.Id).Order(), current.Answers.Select(answer => answer.Id).Order());
        Assert.Equal((1, response.Answers.Count, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task SkippedResponseIsCurrentAndDuplicateInitialWriteIsRejected()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var owner = await fixture.SeedAsync();
        var skipped = Response(owner.Profile, skip: true);
        await fixture.SaveAsync(owner, skipped);
        await Assert.ThrowsAsync<SocialQuestionnaireCurrentResponseConflictException>(() =>
            fixture.SaveAsync(owner, Response(owner.Profile)));

        var current = Assert.IsType<QuestionnaireResponseEntity>(await fixture.ReadAsync(owner));
        Assert.Equal(skipped.Id, current.Id);
        Assert.Equal(QuestionnaireParticipationStatus.Skipped, current.Status);
        Assert.Empty(current.Answers);
        Assert.Equal((1, 0, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task ConcurrentInitialSubmissionsCommitExactlyOneSnapshot()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var owner = await fixture.SeedAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<Guid?> Attempt()
        {
            var response = Response(owner.Profile);
            try
            {
                return await fixture.InTransactionAsync(owner, async repository =>
                {
                    if (Interlocked.Increment(ref callers) == 2) ready.TrySetResult();
                    await ready.Task.WaitAsync(deadline.Token);
                    await repository.CreateInitialResponseAsync(owner.Profile, response, deadline.Token);
                    return (Guid?)response.Id;
                }, deadline.Token);
            }
            catch (SocialQuestionnaireCurrentResponseConflictException) { return null; }
        }

        var outcomes = await Task.WhenAll(Attempt(), Attempt());
        var accepted = Assert.Single(outcomes.OfType<Guid>());
        var current = Assert.IsType<QuestionnaireResponseEntity>(await fixture.ReadAsync(owner));
        Assert.Equal(accepted, current.Id);
        Assert.Equal((1, current.Answers.Count, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task ForeignProfileDiscriminatorAndInactiveModuleAreRejected()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var owner = await fixture.SeedAsync();
        var foreign = await fixture.SeedAsync();
        var hobbies = await fixture.SeedAsync("hobbies");
        var inactive = await fixture.SeedAsync(active: false);
        await fixture.SaveAsync(owner, Response(owner.Profile, skip: true));

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.InTransactionAsync(foreign,
            repository => repository.GetActiveResponseAsync(owner.Profile)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.SaveAsync(foreign, Response(owner.Profile)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.SaveAsync(hobbies, Response(hobbies.Profile)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.SaveAsync(inactive, Response(inactive.Profile)));
        Assert.Equal((1, 0, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task StaleIdsAndUnapprovedTransitionsLeaveTheCurrentResponseIntact()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var owner = await fixture.SeedAsync();
        var response = Response(owner.Profile, skip: true);
        await fixture.SaveAsync(owner, response);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.InTransactionAsync(owner, async repository =>
        {
            await repository.WithdrawResponseAsync(owner.Profile, Guid.NewGuid());
            return true;
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.InTransactionAsync(owner, async repository =>
        {
            await repository.ReplaceCurrentResponseAsync(owner.Profile, response.Id, Response(owner.Profile));
            return true;
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.InTransactionAsync(owner, async repository =>
        {
            await repository.WithdrawResponseAsync(owner.Profile, response.Id);
            return true;
        }));
        Assert.Equal(response.Id, (await fixture.ReadAsync(owner))?.Id);
    }

    [LocalPostgresFact]
    public async Task FailedAnswerInsertAndCancelledWriteLeaveNoPartialRowsOrPointer()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var other = await fixture.SeedAsync();
        var owner = await fixture.SeedAsync();
        var original = Response(other.Profile);
        await fixture.SaveAsync(other, original);
        var reusedAnswerId = original.Answers[0].Id;

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.SaveAsync(owner, Response(owner.Profile, reusedAnswerId: reusedAnswerId)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.InTransactionAsync(owner, async repository =>
        {
            await repository.CreateInitialResponseAsync(owner.Profile, Response(owner.Profile), cancelled.Token);
            return true;
        }));
        Assert.Null(await fixture.ReadAsync(owner));
        Assert.Equal((1, original.Answers.Count, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task RuntimePrivilegesAndCleanupAreRestrictedToTheDisposableDatabase()
    {
        var fixture = await SocialLifecycleDatabase.CreateAsync();
        var database = fixture.Database.DatabaseName;
        var role = fixture.Role;
        try
        {
            await using var context = fixture.Database.Open();
            await using var tx = await context.Database.BeginTransactionAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role', {role}, true)");
            Assert.False(await context.Database.SqlQueryRaw<bool>("""
                SELECT (has_table_privilege('andril_social.social_questionnaire_current_responses','UPDATE,DELETE,TRUNCATE')
                    OR has_table_privilege((SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'andril_identity' AND c.relname = 'private_module_profiles'), 'SELECT')) AS "Value"
                """).SingleAsync());
            Assert.True(await context.Database.SqlQueryRaw<bool>("""
                SELECT row_security_active('andril_social.social_questionnaire_current_responses'::regclass) AS "Value"
                """).SingleAsync());
        }
        finally { await fixture.DisposeAsync(); }

        await using var admin = await SocialLifecycleDatabase.OpenMaintenanceAsync();
        await using var cleanup = new NpgsqlCommand("""
            SELECT NOT EXISTS (SELECT FROM pg_database WHERE datname = $1)
                AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = $2)
            """, admin);
        cleanup.Parameters.AddWithValue(database);
        cleanup.Parameters.AddWithValue(role);
        Assert.Equal(true, await cleanup.ExecuteScalarAsync());
    }

    internal static QuestionnaireResponseEntity Response(Guid profile, bool skip = false, Guid? reusedAnswerId = null)
    {
        var definition = QuestionnaireV1Definition.Create();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var answers = skip ? [] : definition.Questions.Select((question, index) =>
            new QuestionnaireAnswerEntity(index == 0 && reusedAnswerId.HasValue ? reusedAnswerId.Value : Guid.NewGuid(),
                id, question.Id, "3", now)).ToArray();
        return QuestionnaireResponseEntity.Create(id, profile, definition, answers, skip, now);
    }
}

internal sealed class SocialLifecycleDatabase(LocalSocialQuestionnaireDatabase database, string role) : IAsyncDisposable
{
    internal LocalSocialQuestionnaireDatabase Database { get; } = database;
    internal string Role { get; } = role;
    internal sealed record Actor(Guid Account, Guid Subject, Guid Profile) : ICurrentUser
    {
        public Guid? UserId => Subject;
        public bool IsAuthenticated => true;
    }

    internal static async Task<SocialLifecycleDatabase> CreateAsync()
    {
        var database = await LocalSocialQuestionnaireDatabase.CreateAsync();
        var fixture = new SocialLifecycleDatabase(database, "social_lifecycle_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var context = database.Open();
            using var identifiers = new NpgsqlCommandBuilder();
            var role = identifiers.QuoteIdentifier(fixture.Role);
            var grants = $"""
                CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
                GRANT {role} TO CURRENT_USER;
                GRANT USAGE ON SCHEMA andril_social TO {role};
                GRANT SELECT, INSERT ON andril_social.social_questionnaire_responses,
                    andril_social.social_questionnaire_answers, andril_social.social_questionnaire_current_responses TO {role};
                GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid) TO {role};
                """;
            await using var tx = await context.Database.BeginTransactionAsync();
            await context.Database.ExecuteSqlRawAsync(grants);
            await tx.CommitAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task<Actor> SeedAsync(string module = "social", bool active = true)
    {
        var actor = new Actor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var accounts = Database.OpenAccounts();
        await accounts.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({actor.Account}, {actor.Subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({actor.Profile}, {actor.Account}, {module}, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({actor.Account}, {module}, CURRENT_TIMESTAMP, CASE WHEN {module} = 'social' THEN CURRENT_TIMESTAMP END);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({actor.Account}, {module}, {(active ? "active" : "inactive")}, 1);
            """);
        return actor;
    }

    internal async Task<T> InTransactionAsync<T>(Actor actor, Func<PostgresSocialQuestionnaireLifecycleRepository, Task<T>> action,
        CancellationToken token = default)
    {
        await using var accounts = Database.OpenAccounts();
        await using var tx = await accounts.Database.BeginTransactionAsync(token);
        var options = new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(Database.ConnectionString).Options;
        var result = await action(new PostgresSocialQuestionnaireLifecycleRepository(accounts, options, actor, Role));
        await tx.CommitAsync(token);
        return result;
    }

    internal Task SaveAsync(Actor actor, QuestionnaireResponseEntity response) => InTransactionAsync(actor, async repository =>
    {
        await repository.CreateInitialResponseAsync(actor.Profile, response);
        return true;
    });

    internal Task<QuestionnaireResponseEntity?> ReadAsync(Actor actor) =>
        InTransactionAsync(actor, repository => repository.GetActiveResponseAsync(actor.Profile));

    internal async Task<(int Responses, int Answers, long Current)> CountsAsync()
    {
        await using var context = Database.Open();
        return (await context.QuestionnaireResponses.CountAsync(), await context.QuestionnaireAnswers.CountAsync(),
            await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_questionnaire_current_responses").SingleAsync());
    }

    internal static async Task<NpgsqlConnection> OpenMaintenanceAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES"));
        LocalSocialQuestionnaireDatabase.ValidateSettings(settings);
        settings.IncludeErrorDetail = false;
        settings.Pooling = false;
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await using var maintenance = await OpenMaintenanceAsync();
        using var identifiers = new NpgsqlCommandBuilder();
        await using var dropRole = new NpgsqlCommand("DROP ROLE IF EXISTS " + identifiers.QuoteIdentifier(Role), maintenance);
        await dropRole.ExecuteNonQueryAsync();
    }
}
