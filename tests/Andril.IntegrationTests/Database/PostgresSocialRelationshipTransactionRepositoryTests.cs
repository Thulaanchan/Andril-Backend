using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PostgresSocialRelationshipTransactionRepositoryTests
{
    [LocalPostgresFact]
    public async Task FirstInterestIsPrivateAndDuplicateIsIdempotent()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var actor = await fixture.SeedAsync();
        var target = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(actor.Profile, target.Profile);

        var first = await fixture.InterestAsync(actor, pair, true);
        var duplicate = await fixture.InterestAsync(actor, pair, true);
        Assert.True(first.Success && duplicate.Success);
        Assert.Equal(SocialRelationshipState.PendingUnilateral, first.ResultingState);
        Assert.False(first.MatchCreated || duplicate.MatchCreated);
        Assert.Equal(SocialRelationshipState.None, (await fixture.ReadAsync(target, pair)).State);
        Assert.Equal(SocialRelationshipState.PendingUnilateral, (await fixture.ReadAsync(actor, pair)).State);
        var own = await fixture.ExecuteAsync(actor, repository => repository.GetDirectionalInterestAsync(pair, actor.Profile));
        var counterpart = await fixture.ExecuteAsync(target, repository => repository.GetReciprocalInterestAsync(pair, target.Profile));
        Assert.NotNull(own);
        Assert.Equal(own, counterpart);
        Assert.Equal("{}", System.Text.Json.JsonSerializer.Serialize(counterpart));
        Assert.Equal((1L, 0L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task OpposingTransactionsCreateExactlyOneMatchAndPersistInNewContexts()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participants = 0;
        Task<SocialInterestTransactionResult> Attempt(Actor actor) => fixture.ExecuteAsync(actor, async repository =>
        {
            if (Interlocked.Increment(ref participants) == 2) ready.SetResult();
            await ready.Task.WaitAsync(deadline.Token);
            return await repository.RecordInterestAndTryMatchAsync(pair, actor.Profile, true, DateTime.UtcNow, deadline.Token);
        }, deadline.Token);

        var results = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.All(results, result => Assert.True(result.Success));
        var created = Assert.Single(results, result => result.MatchCreated).Match!;
        Assert.Equal(created.MatchId, (await fixture.ReadAsync(first, pair)).ActiveMatch!.MatchId);
        Assert.Equal(created.MatchId, (await fixture.ReadAsync(second, pair)).ActiveMatch!.MatchId);
        var repeat = await fixture.InterestAsync(first, pair, true);
        Assert.True(repeat.Success);
        Assert.False(repeat.MatchCreated);
        Assert.Equal(created.MatchId, repeat.Match!.MatchId);
        Assert.Equal((2L, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task NegativeInterestPreventsMatchingWithoutRevealingTheDecision()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);

        Assert.Equal(SocialRelationshipState.Passed, (await fixture.InterestAsync(first, pair, false)).ResultingState);
        Assert.Equal(SocialRelationshipState.None, (await fixture.ReadAsync(second, pair)).State);
        var positive = await fixture.InterestAsync(second, pair, true);
        Assert.True(positive.Success);
        Assert.Equal(SocialRelationshipState.PendingUnilateral, positive.ResultingState);
        Assert.Null(positive.Match);
        Assert.Equal((2L, 0L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task OutsidersCrossModuleTargetsAndSelfMatchesAreRejected()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var outsider = await fixture.SeedAsync();
        var hobbies = await fixture.SeedAsync("hobbies");
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.ExecuteAsync(outsider,
            repository => repository.GetRelationshipStateAsync(pair, first.Profile)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.InterestAsync(first,
            new SocialProfilePairKey(first.Profile, hobbies.Profile), true));
        Assert.Throws<ArgumentException>(() => new SocialProfilePairKey(first.Profile, first.Profile));
        Assert.Equal((0L, 0L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task RevocationIsIdempotentAndSuppressesTheMatchForBothParticipants()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);
        var match = (await fixture.InterestAsync(second, pair, true)).Match!;

        var revoked = await fixture.ExecuteAsync(first, repository => repository.RevokeMatchAsync(pair, first.Profile, DateTime.UtcNow));
        var repeated = await fixture.ExecuteAsync(second, repository => repository.RevokeMatchAsync(pair, second.Profile, DateTime.UtcNow));
        Assert.True(revoked.Success && repeated.Success);
        Assert.Equal(match.MatchId, revoked.RevokedMatchId);
        Assert.False(repeated.MatchRevoked);
        Assert.Null(repeated.RevokedMatchId);
        Assert.Equal(SocialRelationshipState.Revoked, (await fixture.ReadAsync(first, pair)).State);
        Assert.Null((await fixture.ReadAsync(second, pair)).ActiveMatch);
        Assert.False((await fixture.InterestAsync(first, pair, true)).Success);
        Assert.Equal((2L, 1L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task FailedOuterTransactionRollsBackInterestAndMatchTogether()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);

        await Assert.ThrowsAsync<IntentionalFailure>(() => fixture.ExecuteAsync<SocialInterestTransactionResult>(second, async repository =>
        {
            var outcome = await repository.RecordInterestAndTryMatchAsync(pair, second.Profile, true, DateTime.UtcNow);
            Assert.True(outcome.MatchCreated);
            throw new IntentionalFailure();
        }));

        Assert.Equal((1L, 0L), await fixture.CountsAsync());
        Assert.Equal(SocialRelationshipState.None, (await fixture.ReadAsync(second, pair)).State);
    }

    [LocalPostgresFact]
    public async Task CancellationAtTheCommitBoundaryLeavesNoRows()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteAsync(first, async repository =>
        {
            var result = await repository.RecordInterestAndTryMatchAsync(pair, first.Profile, true, DateTime.UtcNow, cancellation.Token);
            cancellation.Cancel();
            return result;
        }, cancellation.Token));
        Assert.Equal((0L, 0L), await fixture.CountsAsync());
    }

    [LocalPostgresFact]
    public async Task RuntimeRoleCannotDeleteRowsRewritePairsOrInsertAnArbitraryMatch()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        Assert.True(await fixture.CheckRoleAsync());
    }

    [LocalPostgresFact]
    public async Task BlockAndAuthorizedModerationSuppressBothDirectionsAndAuditOnlyChanges()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var moderator = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);
        Assert.True((await fixture.InterestAsync(second, pair, true)).MatchCreated);

        await fixture.Safety(first).SetBlockAsync(second.Profile, true);
        await fixture.Safety(first).SetBlockAsync(second.Profile, true);
        await fixture.Safety(second).SetBlockAsync(first.Profile, false);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.ReadAsync(first, pair));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.InterestAsync(second, pair, true));
        await fixture.Safety(first).SetBlockAsync(second.Profile, false);
        Assert.Equal(SocialRelationshipState.Revoked, (await fixture.ReadAsync(second, pair)).State);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.Safety(first).SetRestrictionAsync(second.Profile, true));
        await fixture.GrantModerationAsync(moderator);
        await fixture.Safety(moderator).SetRestrictionAsync(second.Profile, true);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.ReadAsync(first, pair));
        await fixture.Safety(moderator).SetRestrictionAsync(second.Profile, false);
        Assert.Equal(SocialRelationshipState.Revoked, (await fixture.ReadAsync(first, pair)).State);
        Assert.Equal(4L, await fixture.AuditCountAsync());
    }

    private sealed class IntentionalFailure : Exception;

    private sealed record Actor(Guid Account, Guid Profile, Guid Subject) : ICurrentUser, Andril.Common.Accounts.Onboarding.IOnboardingIdentity
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Subject;
        public bool IsConfirmedFor(Guid subject) => subject == Subject;
    }

    private sealed class RelationshipDatabase(LocalSocialQuestionnaireDatabase fixture, string role) : IAsyncDisposable
    {
        public static async Task<RelationshipDatabase> CreateAsync()
        {
            var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync();
            var role = "social_relationship_runtime_" + Guid.NewGuid().ToString("N");
            try
            {
                await using var context = fixture.Open();
                await using var transaction = await context.Database.BeginTransactionAsync();
                var grants = $"""
                    CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
                    GRANT {role} TO CURRENT_USER;
                    GRANT USAGE ON SCHEMA andril_social TO {role};
                    GRANT SELECT, INSERT, UPDATE (is_positive, recorded_at_utc) ON andril_social.social_directional_interests TO {role};
                    GRANT SELECT, UPDATE (state, revoked_at_utc) ON andril_social.social_matches TO {role};
                    GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid),
                        andril_social.social_reciprocal_interest(uuid, uuid),
                        andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone),
                        andril_social.social_safety_lock(), andril_social.social_safety_decision(uuid, uuid),
                        andril_social.set_social_block(uuid, boolean), andril_social.set_social_restriction(uuid, boolean) TO {role};
                    """;
                await context.Database.ExecuteSqlRawAsync(grants);
                await transaction.CommitAsync();
                return new RelationshipDatabase(fixture, role);
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<Actor> SeedAsync(string module = "social")
        {
            var actor = new Actor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await using var accounts = fixture.OpenAccounts();
            await accounts.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                    VALUES ({actor.Account}, {actor.Subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                    VALUES ({actor.Profile}, {actor.Account}, {module}, CURRENT_TIMESTAMP);
                INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                    VALUES ({actor.Account}, {module}, CURRENT_TIMESTAMP, CASE WHEN {module} = 'social' THEN CURRENT_TIMESTAMP END);
                INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                    VALUES ({actor.Account}, {module}, 'active', 1);
                """);
            return actor;
        }

        public async Task<T> ExecuteAsync<T>(Actor actor, Func<ISocialRelationshipTransactionRepository, Task<T>> action,
            CancellationToken token = default)
        {
            await using var accounts = fixture.OpenAccounts();
            var options = new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.ConnectionString).Options;
            var repository = new PostgresSocialRelationshipTransactionRepository(accounts, options, actor, role);
            var profileRepository = new PrivateProfileRepository(accounts);
            return await profileRepository.WithAuthorizedProfileAsync(actor.Account, "social", _ => { }, async profile =>
            {
                Assert.Equal(actor.Profile, profile.Id);
                return await action(repository);
            }, token);
        }

        public Task<SocialInterestTransactionResult> InterestAsync(Actor actor, SocialProfilePairKey pair, bool positive) =>
            ExecuteAsync(actor, repository => repository.RecordInterestAndTryMatchAsync(pair, actor.Profile, positive, DateTime.UtcNow));

        public Task<SocialRelationshipSnapshot> ReadAsync(Actor actor, SocialProfilePairKey pair) =>
            ExecuteAsync(actor, repository => repository.GetRelationshipStateAsync(pair, actor.Profile));

        public async Task<(long Interests, long Matches)> CountsAsync()
        {
            await using var context = fixture.Open();
            return (
                await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_directional_interests").SingleAsync(),
                await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_matches").SingleAsync());
        }

        public PostgresSocialSafetyRepository Safety(Actor actor) => new(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(fixture.ConnectionString).Options, actor, actor, role);

        public async Task GrantModerationAsync(Actor actor)
        {
            await using var context = fixture.Open();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social.social_moderation_grants(account_id) VALUES ({actor.Account})
                """);
        }

        public async Task<long> AuditCountAsync()
        {
            await using var context = fixture.Open();
            return await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_safety_audit").SingleAsync();
        }

        public async Task<bool> CheckRoleAsync()
        {
            await using var context = fixture.Open();
            return await context.Database.SqlQuery<bool>($"""
                SELECT (NOT has_table_privilege({role}, 'andril_social.social_matches', 'INSERT,DELETE,TRUNCATE')
                    AND NOT has_table_privilege({role}, 'andril_social.social_directional_interests', 'DELETE,TRUNCATE')
                    AND NOT has_column_privilege({role}, 'andril_social.social_matches', 'first_profile_id', 'UPDATE')
                    AND NOT has_column_privilege({role}, 'andril_social.social_directional_interests', 'target_profile_id', 'UPDATE')
                    AND NOT has_schema_privilege({role}, 'andril_social', 'CREATE')) AS "Value"
                """).SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await fixture.DisposeAsync();
            var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP ROLE {role}", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
