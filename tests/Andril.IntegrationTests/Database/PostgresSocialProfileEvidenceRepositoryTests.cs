using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PostgresSocialProfileEvidenceRepositoryTests
{
    [LocalPostgresFact]
    public async Task ExplicitPreferencesAndSkippedQuestionnaireAreReadAcrossScopesWithoutLeakingIdentity()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var role = "social_matching_prefs_" + Guid.NewGuid().ToString("N");
        try
        {
            await SeedAsync(fixture, account, subject, profile, role);
            await using var accounts = fixture.OpenAccounts();
            var user = new Actor(subject);
            var profiles = new OwnerScope(new PrivateProfileRepository(accounts), account);
            var options = new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.ConnectionString).Options;
            var questionnaire = new PostgresSocialQuestionnaireLifecycleRepository(accounts, options, user, role);
            var repository = new PostgresSocialProfileEvidenceRepository(accounts, profiles, user, questionnaire, TimeProvider.System);
            var initial = await repository.ReadSelfAsync();
            Assert.Equal(profile, initial.ProfileId);
            Assert.True(initial.Age >= 18 && initial.IsActive && initial.IsOptedIn);
            Assert.Equal("private", initial.Visibility);
            Assert.Null(initial.Preferences);
            Assert.Null(initial.Questionnaire);

            var settings = new SocialMatchingPreferences(true, new(20, 55, 0, 999), "All", ["Tamil"], ["Friendship"],
                new(SocialEngagementPreference.Reserved, CommunicationPreference.Expressive, PlanningPreference.Structured));
            await repository.SetSelfPreferencesAsync(settings);
            await repository.SetSelfPreferencesAsync(settings);
            var definition = QuestionnaireV1Definition.Create();
            var response = QuestionnaireResponseEntity.Create(Guid.NewGuid(), profile, definition, [], true, DateTimeOffset.UtcNow);
            await profiles.WithAuthorizedProfileAsync("social", async _ =>
            {
                await questionnaire.CreateInitialResponseAsync(profile, response);
                return true;
            }, CancellationToken.None);

            var stored = await repository.ReadSelfAsync();
            Assert.Equal(1, stored.LifecycleRevision);
            Assert.Equal(1, stored.PreferencesRevision);
            Assert.True(stored.Preferences!.Discoverable);
            Assert.Equal(settings.PartnerPreferences, stored.Preferences.PartnerPreferences);
            Assert.Equal(settings.Languages, stored.Preferences.Languages);
            Assert.Equal(settings.ConnectionGoals, stored.Preferences.ConnectionGoals);
            Assert.Equal(QuestionnaireParticipationStatus.Skipped, stored.Questionnaire!.Status);
            Assert.Empty(stored.Questionnaire.Answers);
            Assert.Equal("{}", System.Text.Json.JsonSerializer.Serialize(stored));

            await repository.SetSelfPreferencesAsync(settings with { Discoverable = false });
            var hidden = await repository.ReadSelfAsync();
            Assert.Equal(2, hidden.PreferencesRevision);
            Assert.False(hidden.Preferences!.Discoverable);
        }
        finally
        {
            await fixture.DisposeAsync();
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP ROLE IF EXISTS {role}", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(LocalSocialQuestionnaireDatabase fixture, Guid account, Guid subject, Guid profile, string role)
    {
        await using var db = fixture.OpenAccounts();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({account}, {subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_demographics(account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                VALUES ({account}, DATE '1990-01-01', 'female', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({profile}, {account}, 'social', CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({account}, 'social', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({account}, 'social', 'active', 1);
            """);
        var grants = $"""
            CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
            GRANT {role} TO CURRENT_USER;
            GRANT USAGE ON SCHEMA andril_social TO {role};
            GRANT SELECT, INSERT ON andril_social.social_questionnaire_responses, andril_social.social_questionnaire_answers,
                andril_social.social_questionnaire_current_responses TO {role};
            GRANT SELECT, INSERT, UPDATE ON andril_social.social_matching_preferences TO {role};
            GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid) TO {role};
            """;
        await db.Database.ExecuteSqlRawAsync(grants);
        await transaction.CommitAsync();
    }

    private sealed record Actor(Guid Subject) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Subject;
    }

    private sealed class OwnerScope(PrivateProfileRepository repository, Guid account) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken token) =>
            repository.WithAuthorizedProfileAsync(account, module, _ => { },
                profile => action(new(profile.Id, profile.Module, profile.CreatedAtUtc)), token);

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
