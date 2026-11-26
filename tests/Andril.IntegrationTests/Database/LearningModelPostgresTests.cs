using Andril.Learning.Entities;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>
/// Actual PostgreSQL model verification using the existing M2 opt-in, loopback guard and random
/// database cleanup. No Learning migration/history or production authorization is introduced.
/// Accounts migrations are applied by LocalAccountDatabase; Learning uses generated model DDL.
/// Restricted SQL grants below are fixture-only and do not establish profile ownership or RLS.
/// </summary>
public sealed class LearningModelPostgresTests
{
    private static LearningDbContext Open(LocalAccountDatabase fixture)
    {
        using var accounts = fixture.Open();
        return new(new DbContextOptionsBuilder<LearningDbContext>()
            .UseNpgsql(accounts.Database.GetConnectionString()).Options);
    }

    private static async Task<NpgsqlConnection> Connect(LearningDbContext db)
    {
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    [LocalPostgresFact]
    public async Task GeneratedModelDdlRollsBackReappliesAndPreservesExistingAccountsSchemaWithoutLearningHistory()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync();
        await using var db = Open(fixture);
        await using var connection = await Connect(db);
        const string identityInventory = "SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE schemaname='andril_identity'";
        var before = await Scalar(connection, identityInventory);
        Assert.NotEmpty(before);
        Assert.Equal(new[] { "20261125100000_LearningDurableRevisions", "20261126090000_LearningCvPersistence", "20261126110000_LearningPortfolioPersistence" }, db.Database.GetMigrations());
        var ddl = db.Database.GenerateCreateScript();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Execute(connection, ddl);
            Assert.Equal("7", await Scalar(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='andril_learning'"));
            await transaction.RollbackAsync();
        }
        Assert.Equal("0", await Scalar(connection, "SELECT count(*) FROM pg_namespace WHERE nspname='andril_learning'"));
        await Execute(connection, ddl);
        Assert.Equal("7", await Scalar(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='andril_learning'"));
        Assert.Equal(before, await Scalar(connection, identityInventory));
        Assert.Equal("0", await Scalar(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='andril_learning' AND tablename LIKE '%EFMigrationsHistory%'"));
        Assert.Equal("1", await Scalar(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='andril_identity' AND tablename='__EFMigrationsHistory'"));
    }

    [LocalPostgresFact]
    public async Task PostgreSqlEnforcesUuidModuleKindAndSelectionUniquenessWhileAllowingCrossPurposeOverlap()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync();
        await using var db = Open(fixture);
        await using var connection = await Connect(db);
        await db.Database.MigrateAsync();
        var profile = Guid.NewGuid(); var skill = Guid.NewGuid(); var id = Guid.NewGuid();
        await LearningPostgresFixture.Profile(fixture, new(Guid.NewGuid(), profile));
        string Selection(Guid row, Guid owner, Guid selected, int kind = 1, string module = "learning") =>
            $"INSERT INTO andril_learning.learning_skill_selections(id,learning_profile_id,module,skill_id,kind) VALUES ('{row}','{owner}','{module}','{selected}',{kind})";
        string Goal(Guid row, Guid owner, string module = "learning") =>
            $"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES ('{row}','{owner}','{module}','synthetic')";
        var invalid = new (string Sql, string Constraint)[]
        {
            (Selection(Guid.Empty, profile, skill), "ck_learning_skill_selection_id"),
            (Selection(Guid.NewGuid(), Guid.Empty, skill), "ck_learning_skill_selection_profile"),
            (Selection(Guid.NewGuid(), profile, Guid.Empty), "ck_learning_skill_selection_skill"),
            (Selection(Guid.NewGuid(), profile, skill, 3), "ck_learning_skill_selection_kind"),
            (Selection(Guid.NewGuid(), profile, skill, 1, "social"), "ck_learning_skill_selection_module"),
            (Goal(Guid.Empty, profile), "ck_learning_personal_goal_id"),
            (Goal(Guid.NewGuid(), Guid.Empty), "ck_learning_personal_goal_profile"),
            (Goal(Guid.NewGuid(), profile, "social"), "ck_learning_personal_goal_module")
        };
        foreach (var attempt in invalid)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, attempt.Sql));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal(attempt.Constraint, error.ConstraintName);
        }
        await Execute(connection, Selection(id, profile, skill));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, Selection(Guid.NewGuid(), profile, skill)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("uq_learning_skill_selections_profile_purpose_skill", duplicate.ConstraintName);
        await Execute(connection, Selection(Guid.NewGuid(), profile, skill, 2));
        var independentProfile = Guid.NewGuid();
        await LearningPostgresFixture.Profile(fixture, new(Guid.NewGuid(), independentProfile));
        await Execute(connection, Selection(Guid.NewGuid(), independentProfile, skill));
        Assert.Equal("3", await Scalar(connection, "SELECT count(*) FROM andril_learning.learning_skill_selections"));
        Assert.Equal("0", await Scalar(connection, "SELECT count(*) FROM andril_learning.learning_personal_goals"));
    }

    [LocalPostgresFact]
    public async Task EfRoundTripAndRollbackPreserveGoalsSelectionIdentitiesAndGoalLookupIndex()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync();
        await using var db = Open(fixture);
        await using var connection = await Connect(db);
        await db.Database.MigrateAsync();
        var preferences = new LearningPreferences(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(fixture, new(preferences.AccountId, preferences.LearningProfileId));
        var skill = Guid.NewGuid(); preferences.UpdateSelections([], [skill], []);
        var goal = preferences.CreateGoal("Synthetic தமிழ் goal"); var id = Guid.NewGuid();
        db.SkillSelections.Add(LearningSkillSelection.CreateFromPreferences(id, preferences, skill, LearningSkillSelectionKind.Teaching));
        db.PersonalGoals.Add(LearningPersonalGoal.CreateFromPreferences(preferences, goal.Id));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var row = await db.SkillSelections.SingleAsync(); db.SkillSelections.Remove(row);
            await db.SaveChangesAsync(); await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(id, (await db.SkillSelections.SingleAsync()).Id);
        var stored = await db.PersonalGoals.SingleAsync();
        Assert.Equal(goal.Id, stored.Id); Assert.Equal(goal.Text, stored.Text);
        Assert.Equal(preferences.LearningProfileId, stored.LearningProfileId); Assert.Equal("learning", stored.Module);
        var index = await Scalar(connection, "SELECT indexdef FROM pg_indexes WHERE schemaname='andril_learning' AND indexname='ix_learning_personal_goals_profile_module'");
        Assert.Contains("(learning_profile_id, module)", index); Assert.DoesNotContain("UNIQUE", index);
    }

    [LocalPostgresFact]
    public async Task RestrictedFixtureRoleCannotReadAccountsOrAnotherDatabaseOrCreateLearningObjects()
    {
        await using var fixture = await LocalAccountDatabase.CreateAsync();
        await using var db = Open(fixture);
        await using var connection = await Connect(db);
        await db.Database.MigrateAsync();
        var role = "andril_learning_test_" + Guid.NewGuid().ToString("N");
        await Execute(connection, $"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS");
        try
        {
            await Execute(connection, $"GRANT {role} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE");
            await Execute(connection, $"GRANT USAGE ON SCHEMA andril_learning TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA andril_learning TO {role}");
            await Execute(connection, $"GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text) TO {role}");
            await Execute(connection, $"SET ROLE {role}");
            Assert.Equal("0", await Scalar(connection, "SELECT count(*) FROM andril_learning.learning_personal_goals"));
            foreach (var sql in new[] { "SELECT count(*) FROM andril_identity.accounts", "CREATE TABLE andril_learning.forbidden(id integer)", "CREATE DATABASE forbidden_learning_test" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, sql));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
            Assert.Equal("False", await Scalar(connection, "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname=current_user"));
            // No ownership/RLS grants are inferred from these test-only SQL privileges.
        }
        finally
        {
            await Execute(connection, "RESET ROLE");
            Assert.Equal(await Scalar(connection, "SELECT tableowner FROM pg_tables WHERE schemaname='andril_learning' AND tablename='learning_personal_goals'"),
                await Scalar(connection, "SELECT current_user"));
            Assert.Equal("True", await Scalar(connection, "SELECT current_user = session_user"));
            // SET membership deliberately does not inherit this role's privileges. DROP OWNED
            // would require those privileges after RESET ROLE. No objects can be created by
            // the restricted role: revoke our explicit grants as their fixture owner instead.
            await Execute(connection, $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}");
            await Execute(connection, $"REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}");
            await Execute(connection, $"REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}");
            await Execute(connection, $"DROP ROLE {role}");
            Assert.Equal("0", await Scalar(connection, $"SELECT count(*) FROM pg_roles WHERE rolname='{role}'"));
        }
    }
}
