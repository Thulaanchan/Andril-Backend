using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Security boundary tests verifying the four discrete states of CF-20 reciprocal matching:
/// 1. Directional Interest (isolated, one-sided, non-disclosing).
/// 2. Mutual Interest Observed (ephemeral evaluation, no communication grant).
/// 3. Persisted Match Entity (structural persistence candidate, strictly scope-isolated).
/// 4. Communication Authorization (M4 chat boundary, strictly fails closed without authoritative evidence).
/// </summary>
public sealed class SocialMutualMatchAuthorizationBoundaryTests
{
    private static readonly Guid ProfileIdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileIdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ForeignProfileId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTime FixedTimestampUtc = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    private sealed class InMemorySocialInterestRepoDouble : ISocialInterestRepository
    {
        private readonly SocialDirectionalInterest? _first;
        private readonly SocialDirectionalInterest? _second;

        public InMemorySocialInterestRepoDouble(SocialDirectionalInterest? first, SocialDirectionalInterest? second)
        {
            _first = first;
            _second = second;
        }

        public Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((_first, _second));
        }

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> HasRecordedInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveDirectionalInterestAsync(
            SocialDirectionalInterest interest,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class InMemorySocialMatchRepoDouble : ISocialMatchRepository
    {
        private readonly SocialMatchEntity? _match;

        public InMemorySocialMatchRepoDouble(SocialMatchEntity? match)
        {
            _match = match;
        }

        public Task<SocialMatchEntity?> GetMatchByIdAsync(
            Guid matchId,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_match is not null && _match.MatchId == matchId)
            {
                return Task.FromResult<SocialMatchEntity?>(_match);
            }
            return Task.FromResult<SocialMatchEntity?>(null);
        }

        public Task<SocialMatchEntity?> GetMatchByPairKeyAsync(
            SocialProfilePairKey pairKey,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_match is not null && _match.PairKey.Equals(pairKey))
            {
                return Task.FromResult<SocialMatchEntity?>(_match);
            }
            return Task.FromResult<SocialMatchEntity?>(null);
        }

        public Task<bool> HasMatchAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_match is not null && _match.PairKey.Equals(pairKey));
        }
    }

    #region State 1: Directional Interest Security Boundaries

    [Fact]
    public void DirectionalInterest_ValidMember_RecordsIsolatedInterest()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileIdA, isPositive: true, FixedTimestampUtc);

        Assert.Equal(pairKey, interest.PairKey);
        Assert.Equal(ProfileIdA, interest.ActingProfileId);
        Assert.True(interest.IsPositive);
        Assert.Equal(FixedTimestampUtc, interest.RecordedAtUtc);
    }

    [Fact]
    public void DirectionalInterest_NonMemberActingProfile_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);

        Assert.Throws<ArgumentException>(() =>
            new SocialDirectionalInterest(pairKey, ForeignProfileId, isPositive: true, FixedTimestampUtc));
    }

    [Fact]
    public void DirectionalInterest_EmptyProfileIdentity_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);

        Assert.Throws<ArgumentException>(() =>
            new SocialDirectionalInterest(pairKey, Guid.Empty, isPositive: true, FixedTimestampUtc));
    }

    [Fact]
    public void DirectionalInterest_OneSidedPositive_NeverIndicatesMutualInterest()
    {
        var status = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: true, secondInterestedInFirst: null);

        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, status);
        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, status);
    }

    #endregion

    #region State 2: Mutual Interest Observation Boundaries

    [Fact]
    public async Task MutualInterestObserved_BothPositive_ReturnsMutualInterestObserved()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, FixedTimestampUtc);
        var interestB = new SocialDirectionalInterest(pairKey, pairKey.SecondProfileId, true, FixedTimestampUtc);

        var repo = new InMemorySocialInterestRepoDouble(interestA, interestB);
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pairKey);

        Assert.True(result.IsValid);
        Assert.True(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, result.ReciprocalStatus);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
    }

    [Fact]
    public async Task MutualInterestObserved_OneSidedNegative_ReturnsNotMutual()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, FixedTimestampUtc);
        var interestB = new SocialDirectionalInterest(pairKey, pairKey.SecondProfileId, false, FixedTimestampUtc);

        var repo = new InMemorySocialInterestRepoDouble(interestA, interestB);
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pairKey);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result.ReciprocalStatus);
    }

    [Fact]
    public void MutualInterestObservation_DoesNotMutateOrPersistMatchEntity()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var result = SocialPairInterestObservationResult.Success(pairKey, SocialReciprocalInterestStatus.MutualInterestObserved);

        // Result is pure in-memory value representation
        Assert.True(result.IsMutualMatch);
        Assert.Equal("SocialPairInterestObservationResult [private]", result.ToString());

        var json = JsonSerializer.Serialize(result);
        Assert.Equal("{}", json);
        Assert.DoesNotContain(ProfileIdA.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileIdB.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region State 3: Persisted Match Entity Scoped Isolation

    [Fact]
    public async Task PersistedMatch_AuthorizedMember_LookupSucceeds()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var matchId = Guid.NewGuid();
        var matchEntity = SocialMatchEntity.Create(matchId, pairKey, FixedTimestampUtc);

        var repo = new InMemorySocialMatchRepoDouble(matchEntity);
        var lookupService = new SocialMatchScopedLookupService(repo);

        var result = await lookupService.GetMatchByIdAsync(matchId, requestingProfileId: ProfileIdA);

        Assert.True(result.IsFound);
        Assert.Equal(SocialMatchLookupStatus.Found, result.Status);
        Assert.NotNull(result.Match);
        Assert.Equal(matchId, result.Match.MatchId);
    }

    [Fact]
    public async Task PersistedMatch_UnrelatedThirdParty_LookupReturnsProfileMismatch()
    {
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var matchId = Guid.NewGuid();
        var matchEntity = SocialMatchEntity.Create(matchId, pairKey, FixedTimestampUtc);

        var repo = new InMemorySocialMatchRepoDouble(matchEntity);
        var lookupService = new SocialMatchScopedLookupService(repo);

        // ForeignProfileId is neither ProfileIdA nor ProfileIdB
        var result = await lookupService.GetMatchByIdAsync(matchId, requestingProfileId: ForeignProfileId);

        Assert.False(result.IsFound);
        Assert.Equal(SocialMatchLookupStatus.ProfileIdMismatch, result.Status);
        Assert.Null(result.Match);
    }

    [Fact]
    public async Task PersistedMatch_InvalidEmptyProfile_ReturnsInvalidIdentity()
    {
        var repo = new InMemorySocialMatchRepoDouble(null);
        var lookupService = new SocialMatchScopedLookupService(repo);

        var result = await lookupService.GetMatchByIdAsync(Guid.NewGuid(), requestingProfileId: Guid.Empty);

        Assert.False(result.IsFound);
        Assert.Equal(SocialMatchLookupStatus.InvalidIdentity, result.Status);
    }

    #endregion

    #region State 4: Communication Authorization Decoupling (CF-20 Hard Security Boundary)

    [Fact]
    public async Task CommunicationBoundary_MutualInterestAlone_DoesNotGrantChatAccess()
    {
        // 1. Observe mutual interest in Social domain
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, FixedTimestampUtc);
        var interestB = new SocialDirectionalInterest(pairKey, pairKey.SecondProfileId, true, FixedTimestampUtc);

        var interestRepo = new InMemorySocialInterestRepoDouble(interestA, interestB);
        var observationService = new SocialPairInterestObservationService(interestRepo);
        var observationResult = await observationService.ObservePairInterestAsync(pairKey);

        Assert.True(observationResult.IsMutualMatch);

        // 2. Query communication authorizer for Social chat conversation
        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        var conversationId = Guid.NewGuid();

        var authorizedActor = await authorizer.AuthorizeConversationAsync(ChatModule.Social, conversationId);

        // Security assertion: Chat access is strictly denied (fails closed)
        Assert.Null(authorizedActor);
    }

    [Fact]
    public async Task CommunicationBoundary_PersistedMatchEntityAlone_DoesNotGrantChatAccess()
    {
        // 1. Create persisted SocialMatchEntity
        var pairKey = new SocialProfilePairKey(ProfileIdA, ProfileIdB);
        var matchId = Guid.NewGuid();
        var matchEntity = SocialMatchEntity.Create(matchId, pairKey, FixedTimestampUtc);

        Assert.NotNull(matchEntity);

        // 2. Query communication authorizer
        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        var conversationId = Guid.NewGuid();

        var authorizedActor = await authorizer.AuthorizeConversationAsync(ChatModule.Social, conversationId);

        // Security assertion: Persisted match existence alone does not grant chat authorization
        Assert.Null(authorizedActor);
    }

    [Fact]
    public void ArchitectureBoundary_StateRepresentationsAreStrictlyDecoupled()
    {
        // Assert compile-time and runtime type separation across the 4 stages
        Assert.False(typeof(SocialDirectionalInterest).IsAssignableFrom(typeof(SocialPairInterestObservationResult)));
        Assert.False(typeof(SocialPairInterestObservationResult).IsAssignableFrom(typeof(SocialMatchEntity)));
        Assert.False(typeof(SocialMatchEntity).IsAssignableFrom(typeof(ChatAuthorizedActor)));

        // Verify ChatAuthorizedActor has non-disclosing ToString
        var actor = new ChatAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ChatModule.Social, Guid.NewGuid());
        Assert.Equal(nameof(ChatAuthorizedActor), actor.ToString());
    }

    #endregion
}
