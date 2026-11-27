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

    [LocalPostgresFact]
    public async Task ConsentedLocationsUseCoarseCellsAndWithdrawalRemovesDistance()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        await fixture.PrepareMatchingAsync(first);
        await fixture.PrepareMatchingAsync(second);
        await fixture.LocationAsync(first).UpdateSelfAsync(6.912345m, 79.911111m, consent: true);
        await fixture.LocationAsync(second).UpdateSelfAsync(7.021234m, 79.923456m, consent: true);

        var firstStored = await fixture.LocationRecordAsync(first);
        Assert.Equal((6.90m, 79.90m), firstStored);
        var cards = await fixture.DiscoveryAsync(first);
        var card = Assert.Single(cards);
        Assert.Equal(second.Profile, card.Evidence.ProfileId);
        Assert.Equal(11, card.ApproximateDistanceKm);
        Assert.Null(card.Evidence.QuestionnaireAnswers);
        Assert.Equal(card.CachedRevision, card.CommittedRevision);

        await fixture.LocationAsync(second).WithdrawSelfAsync();
        Assert.Equal((null, null), await fixture.LocationRecordAsync(second));
        var withoutConsent = Assert.Single(await fixture.DiscoveryAsync(first));
        Assert.Null(withoutConsent.ApproximateDistanceKm);
        Assert.NotEqual(card.CommittedRevision, withoutConsent.CommittedRevision);
    }

    [LocalPostgresFact]
    public async Task AConcurrentBlockWaitsForInterestThenRevokesTheCommittedMatch()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var like = fixture.ExecuteAsync(second, async repository =>
        {
            var result = await repository.RecordInterestAndTryMatchAsync(pair, second.Profile, true, DateTime.UtcNow, deadline.Token);
            held.SetResult();
            await release.Task.WaitAsync(deadline.Token);
            return result;
        }, deadline.Token);
        await held.Task.WaitAsync(deadline.Token);
        var block = fixture.Safety(first).SetBlockAsync(second.Profile, true, deadline.Token);
        try { await fixture.WaitForAdvisoryWaiterAsync(deadline.Token); }
        finally { release.TrySetResult(); }
        Assert.True((await like).MatchCreated);
        await block;
        Assert.Equal(0L, await fixture.ActiveMatchesAsync());
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.ReadAsync(second, pair));
    }

    [LocalPostgresFact]
    public async Task DeactivationAndMatchAreSerializedAndACommittedDeactivationRevokesTheMatch()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var match = fixture.ExecuteAsync(second, async repository =>
        {
            var result = await repository.RecordInterestAndTryMatchAsync(pair, second.Profile, true, DateTime.UtcNow, deadline.Token);
            held.SetResult();
            await release.Task.WaitAsync(deadline.Token);
            return result;
        }, deadline.Token);
        await held.Task.WaitAsync(deadline.Token);
        var deactivate = fixture.DeactivateAsync(first, deadline.Token);
        try { await fixture.WaitForAdvisoryWaiterAsync(deadline.Token); }
        finally { release.TrySetResult(); }
        Assert.True((await match).MatchCreated);
        await deactivate;
        Assert.Equal(0L, await fixture.ActiveMatchesAsync());
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.ReadAsync(second, pair));
    }

    [LocalPostgresFact]
    public async Task AnAuthorizedReadQueuedBehindRevocationCannotReleaseTheOldMatch()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync();
        var pair = new SocialProfilePairKey(first.Profile, second.Profile);
        await fixture.InterestAsync(first, pair, true);
        Assert.True((await fixture.InterestAsync(second, pair, true)).MatchCreated);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoke = fixture.ExecuteAsync(first, async repository =>
        {
            var result = await repository.RevokeMatchAsync(pair, first.Profile, DateTime.UtcNow, deadline.Token);
            held.SetResult();
            await release.Task.WaitAsync(deadline.Token);
            return result;
        }, deadline.Token);
        await held.Task.WaitAsync(deadline.Token);
        var read = fixture.ExecuteAsync(second, repository => repository.GetRelationshipStateAsync(pair, second.Profile, deadline.Token), deadline.Token);
        try { await fixture.WaitForAdvisoryWaiterAsync(deadline.Token); }
        finally { release.TrySetResult(); }
        Assert.True((await revoke).MatchRevoked);
        Assert.Null((await read).ActiveMatch);
    }

    [LocalPostgresFact]
    public async Task DiscoveryOmitsHiddenUnderageInactiveRestrictedAndBlockedCandidates()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var caller = await fixture.SeedAsync();
        var candidate = await fixture.SeedAsync();
        var unauthorized = await fixture.SeedAsync("hobbies");
        await fixture.PrepareMatchingAsync(caller);
        await fixture.PrepareMatchingAsync(candidate);
        await fixture.LocationAsync(caller).UpdateSelfAsync(6.9m, 79.9m, consent: true);
        await fixture.LocationAsync(candidate).UpdateSelfAsync(7.0m, 79.9m, consent: true);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.LocationAsync(unauthorized).UpdateSelfAsync(7.0m, 79.9m, consent: true));

        var first = Assert.Single(await fixture.DiscoveryAsync(caller));
        await fixture.SetDiscoveryAsync(candidate, false);
        Assert.Empty(await fixture.DiscoveryAsync(caller));
        await fixture.SetDiscoveryAsync(candidate, true);
        var later = Assert.Single(await fixture.DiscoveryAsync(caller));
        Assert.False(SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(first.CachedRevision, later.CommittedRevision).IsCurrent);

        await fixture.SetAdultAsync(candidate, false);
        Assert.Empty(await fixture.DiscoveryAsync(caller));
        await fixture.SetAdultAsync(candidate, true);
        await fixture.GrantModerationAsync(caller);
        await fixture.Safety(caller).SetRestrictionAsync(candidate.Profile, true);
        Assert.Empty(await fixture.DiscoveryAsync(caller));
        await fixture.Safety(caller).SetRestrictionAsync(candidate.Profile, false);
        await fixture.Safety(candidate).SetBlockAsync(caller.Profile, true);
        Assert.Empty(await fixture.DiscoveryAsync(caller));
        Assert.Empty(await fixture.DiscoveryAsync(candidate));
        await fixture.Safety(candidate).SetBlockAsync(caller.Profile, false);
        Assert.Single(await fixture.DiscoveryAsync(caller));
        await fixture.DeactivateAsync(candidate, CancellationToken.None);
        Assert.Empty(await fixture.DiscoveryAsync(caller));
    }

    [LocalPostgresFact]
    public async Task CommittedRevisionsSuppressContradictoryRowsAndExpiredDistances()
    {
        await using var fixture = await RelationshipDatabase.CreateAsync();
        var caller = await fixture.SeedAsync();
        var candidate = await fixture.SeedAsync();
        await fixture.PrepareMatchingAsync(caller);
        await fixture.PrepareMatchingAsync(candidate);
        await fixture.LocationAsync(caller).UpdateSelfAsync(6.9m, 79.9m, consent: true);
        await fixture.LocationAsync(candidate).UpdateSelfAsync(7.0m, 79.9m, consent: true);
        var original = Assert.Single(await fixture.DiscoveryAsync(caller));
        await fixture.LocationAsync(candidate).UpdateSelfAsync(7.1m, 79.9m, consent: true);
        var current = Assert.Single(await fixture.DiscoveryAsync(caller));
        var owner = await fixture.DiscoveryCallerAsync(caller);
        Assert.NotNull(owner);
        Assert.NotEqual(original.CommittedRevision, current.CommittedRevision);
        var clean = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(owner.Evidence, owner.CachedRevision,
            owner.CommittedRevision, [current]);
        Assert.Equal(candidate.Profile, Assert.Single(clean).CandidateProfileId);
        Assert.Equal("{}", System.Text.Json.JsonSerializer.Serialize(Assert.Single(clean)));
        Assert.Empty(SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(owner.Evidence, owner.CachedRevision,
            owner.CommittedRevision, [original, current]));

        await fixture.ExpireLocationAsync(candidate);
        var expired = Assert.Single(await fixture.DiscoveryAsync(caller));
        Assert.Null(expired.ApproximateDistanceKm);
        Assert.Empty(SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(owner.Evidence, owner.CachedRevision,
            owner.CommittedRevision, [expired]));
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
                    GRANT SELECT, INSERT, UPDATE ON andril_social.social_location_evidence TO {role};
                    GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid),
                        andril_social.social_reciprocal_interest(uuid, uuid),
                        andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone),
                        andril_social.social_safety_lock(), andril_social.social_safety_decision(uuid, uuid),
                        andril_social.set_social_block(uuid, boolean), andril_social.set_social_restriction(uuid, boolean),
                        andril_social.read_social_matching_evidence(uuid, uuid, integer) TO {role};
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

        private static readonly SocialLocationSettings LocationSettings = new(0.10m, TimeSpan.FromMinutes(5));

        public async Task PrepareMatchingAsync(Actor actor)
        {
            await using var db = fixture.OpenAccounts();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_demographics(account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                    VALUES ({actor.Account}, DATE '1990-01-01', 'female', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                UPDATE andril_identity.private_module_profiles SET display_name = {"Social-" + actor.Profile.ToString("N")[..8]}
                    WHERE id = {actor.Profile};
                INSERT INTO andril_social.social_matching_preferences(profile_id, module, discoverable, show_me,
                    min_age, max_age, min_distance_km, max_distance_km, languages, connection_goals, revision)
                    VALUES ({actor.Profile}, 'social', true, 'All', 18, 99, 0, 100, ARRAY['Tamil'], ARRAY['Friendship'], 1);
                """);
        }

        public LocationScope LocationAsync(Actor actor) => new(this, actor);

        public sealed class LocationScope(RelationshipDatabase database, Actor actor)
        {
            public Task UpdateSelfAsync(decimal latitude, decimal longitude, bool consent) => database.RunLocationAsync(actor,
                repository => repository.UpdateSelfAsync(latitude, longitude, consent));

            public Task WithdrawSelfAsync() => database.RunLocationAsync(actor, repository => repository.WithdrawSelfAsync());
        }

        private async Task RunLocationAsync(Actor actor, Func<PostgresSocialLocationRepository, Task> action)
        {
            await using var accounts = fixture.OpenAccounts();
            var owner = new OwnerProfileService(new PrivateProfileRepository(accounts), actor.Account);
            var scope = new PostgresSocialQuestionnaireLifecycleRepository(accounts,
                new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.ConnectionString).Options, actor, role);
            await action(new PostgresSocialLocationRepository(owner, scope, LocationSettings, TimeProvider.System));
        }

        public async Task<IReadOnlyList<SocialAuthoritativeCandidateItem>> DiscoveryAsync(Actor actor)
        {
            await using var accounts = fixture.OpenAccounts();
            var owner = new OwnerProfileService(new PrivateProfileRepository(accounts), actor.Account);
            var scope = new PostgresSocialQuestionnaireLifecycleRepository(accounts,
                new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.ConnectionString).Options, actor, role);
            var evidence = new Andril.API.Authorization.Social.PostgresSocialMatchingEvidenceProvider(
                new PostgresSocialMatchingEvidenceReader(scope), LocationSettings, TimeProvider.System);
            return await owner.WithAuthorizedProfileAsync("social", profile => evidence.GetCandidateBatchAsync(profile.ProfileId, 10), CancellationToken.None);
        }

        public async Task<(decimal? Latitude, decimal? Longitude)> LocationRecordAsync(Actor actor)
        {
            await using var db = fixture.Open();
            var row = await db.Database.SqlQuery<LocationRecord>($"""
                SELECT latitude_cell AS "Latitude", longitude_cell AS "Longitude"
                FROM andril_social.social_location_evidence WHERE profile_id = {actor.Profile}
                """).SingleAsync();
            return (row.Latitude, row.Longitude);
        }

        private sealed record LocationRecord(decimal? Latitude, decimal? Longitude);

        private sealed class OwnerProfileService(PrivateProfileRepository repository, Guid account) : IPrivateProfileService
        {
            public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken token) =>
                repository.WithAuthorizedProfileAsync(account, module, _ => { },
                    profile => action(new(profile.Id, profile.Module, profile.CreatedAtUtc)), token);

            public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        public async Task WaitForAdvisoryWaiterAsync(CancellationToken token)
        {
            await using var context = fixture.Open();
            while (!await context.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """).SingleAsync(token)) await Task.Delay(25, token);
        }

        public async Task<long> ActiveMatchesAsync()
        {
            await using var context = fixture.Open();
            return await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_matches WHERE state = 'active'").SingleAsync();
        }

        public async Task DeactivateAsync(Actor actor, CancellationToken token)
        {
            await using var context = fixture.OpenAccounts();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_identity.account_module_lifecycles SET state = 'inactive', revision = revision + 1,
                    state_changed_at_utc = CURRENT_TIMESTAMP WHERE account_id = {actor.Account} AND module = 'social'
                """, token);
        }

        public async Task SetDiscoveryAsync(Actor actor, bool discoverable)
        {
            await using var db = fixture.Open();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_social.social_matching_preferences SET discoverable = {discoverable}, revision = revision + 1
                WHERE profile_id = {actor.Profile}
                """);
        }

        public async Task SetAdultAsync(Actor actor, bool adult)
        {
            await using var db = fixture.OpenAccounts();
            var birth = adult ? new DateOnly(1990, 1, 1) : new DateOnly(2015, 1, 1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_identity.account_demographics SET date_of_birth = {birth}, updated_at_utc = CURRENT_TIMESTAMP
                WHERE account_id = {actor.Account}
                """);
        }

        public async Task<Andril.API.Authorization.Social.SocialDiscoveryCallerBundle?> DiscoveryCallerAsync(Actor actor)
        {
            await using var accounts = fixture.OpenAccounts();
            var owner = new OwnerProfileService(new PrivateProfileRepository(accounts), actor.Account);
            var scope = new PostgresSocialQuestionnaireLifecycleRepository(accounts,
                new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.ConnectionString).Options, actor, role);
            var evidence = new Andril.API.Authorization.Social.PostgresSocialMatchingEvidenceProvider(
                new PostgresSocialMatchingEvidenceReader(scope), LocationSettings, TimeProvider.System);
            return await owner.WithAuthorizedProfileAsync("social", profile => evidence.GetCallerBundleAsync(profile.ProfileId), CancellationToken.None);
        }

        public async Task ExpireLocationAsync(Actor actor)
        {
            await using var db = fixture.Open();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_social.social_location_evidence SET observed_at_utc = CURRENT_TIMESTAMP - interval '1 day',
                    revision = revision + 1 WHERE profile_id = {actor.Profile}
                """);
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
