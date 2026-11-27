using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialRelationshipAccessServiceTests
{
    private sealed class StubPrivateProfileService : IPrivateProfileService
    {
        private readonly PrivateProfileDto _profile;

        public StubPrivateProfileService(PrivateProfileDto profile)
        {
            _profile = profile;
        }

        public async Task<T> WithAuthorizedProfileAsync<T>(
            string module,
            Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(_profile.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProfileAccessDeniedException();
            }

            return await action(_profile);
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class StubTransactionRepository : ISocialRelationshipTransactionRepository
    {
        public bool RecordInterestCalled { get; private set; }
        public bool RevokeMatchCalled { get; private set; }
        public SocialProfilePairKey? LastPairKey { get; private set; }
        public Guid LastActingProfileId { get; private set; }
        public bool LastIsPositive { get; private set; }

        public SocialInterestTransactionResult RecordInterestResult { get; set; } =
            SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.PendingUnilateral);

        public SocialRelationshipSnapshot SnapshotResult { get; set; }

        public bool ShouldThrow { get; set; }

        public SocialRevocationTransactionResult RevokeResult { get; set; } =
            new(success: true, matchRevoked: true, revokedMatchId: Guid.NewGuid(), revokedAtUtc: DateTime.UtcNow);

        public StubTransactionRepository(SocialProfilePairKey pairKey)
        {
            SnapshotResult = new SocialRelationshipSnapshot(pairKey, SocialRelationshipState.None);
        }

        public Task<SocialRelationshipSnapshot> GetRelationshipStateAsync(
            SocialProfilePairKey pairKey,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository failure");
            return Task.FromResult(SnapshotResult);
        }

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository failure");
            return Task.FromResult<SocialDirectionalInterest?>(null);
        }

        public Task<SocialDirectionalInterest?> GetReciprocalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository failure");
            return Task.FromResult<SocialDirectionalInterest?>(null);
        }

        public Task<SocialInterestTransactionResult> RecordInterestAndTryMatchAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            bool isPositive,
            DateTime timestampUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository failure");
            RecordInterestCalled = true;
            LastPairKey = pairKey;
            LastActingProfileId = actingProfileId;
            LastIsPositive = isPositive;
            return Task.FromResult(RecordInterestResult);
        }

        public Task<SocialRevocationTransactionResult> RevokeMatchAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            DateTime timestampUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository failure");
            RevokeMatchCalled = true;
            LastPairKey = pairKey;
            LastActingProfileId = actingProfileId;
            return Task.FromResult(RevokeResult);
        }
    }

    private sealed class StubEvidenceProvider : ISocialRelationshipEvidenceProvider
    {
        public SocialDiscoveryCallerEvidence? CallerEvidence { get; set; }
        public SocialDiscoveryCandidateEvidence? CandidateEvidence { get; set; }

        public Task<SocialDiscoveryCallerEvidence?> GetCallerEvidenceAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CallerEvidence);
        }

        public Task<SocialDiscoveryCandidateEvidence?> GetCandidateEvidenceAsync(
            Guid candidateProfileId,
            Guid callerProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CandidateEvidence);
        }
    }

    private static (Guid ProfileA, Guid ProfileB, SocialProfilePairKey PairKey) CreatePair()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        return (idA, idB, SocialProfilePairKey.Create(idA, idB));
    }

    private static SocialDiscoveryCandidateEvidence CreateCandidateEvidence(
        Guid candidateProfileId,
        int age = 26,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasAuthoritativeEvidence = true)
    {
        return new SocialDiscoveryCandidateEvidence(
            candidateProfileId,
            age,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasAuthoritativeEvidence);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenAuthorizedAndReciprocalAffirmative_ReturnsMatchEstablished()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var matchId = Guid.NewGuid();
        var matchEntity = SocialMatchEntity.Create(matchId, pair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = SocialInterestTransactionResult.MatchEstablished(matchEntity)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.MatchCreated);
        Assert.Equal(matchId, result.MatchId);
        Assert.Equal(SocialRelationshipState.Matched, result.VisibleState);
        Assert.True(stubRepo.RecordInterestCalled);
        Assert.Equal(idA, stubRepo.LastActingProfileId);
        Assert.True(stubRepo.LastIsPositive);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenAuthorizedAndUnreciprocatedLike_ConcealsCandidateDecisionAndReturnsPendingUnilateral()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.PendingUnilateral)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
        Assert.Equal(SocialRelationshipState.PendingUnilateral, result.VisibleState);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenAuthorizedAndPass_ReturnsPassed()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.Passed)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
        Assert.Equal(SocialRelationshipState.Passed, result.VisibleState);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenWrongModule_ThrowsProfileAccessDeniedException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "learning", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenEmptyProfileId_ThrowsProfileAccessDeniedException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(Guid.Empty, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenSelfTarget_ThrowsProfileAccessDeniedException()
    {
        var (idA, _, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idA, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenActorUnderage_ThrowsProfileAccessDeniedException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 17, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenCandidateUnderage_ThrowsProfileAccessDeniedException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, age: 17)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenBlockedByCandidate_ThrowsProfileAccessDeniedException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26, isCallerBlockedByCandidate: true)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));
    }

    [Fact]
    public async Task GetRelationshipStatusAsync_WhenActiveMatchExists_ReturnsMatched()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var matchId = Guid.NewGuid();
        var match = SocialMatchEntity.Create(matchId, pair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            SnapshotResult = new SocialRelationshipSnapshot(pair, SocialRelationshipState.Matched, match)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.GetRelationshipStatusAsync(idB, CancellationToken.None);

        Assert.True(result.HasActiveMatch);
        Assert.Equal(matchId, result.MatchId);
        Assert.NotNull(result.MatchedAtUtc);
    }

    [Fact]
    public async Task GetRelationshipStatusAsync_WithoutCurrentTargetEvidence_SuppressesTheMatch()
    {
        var (owner, target, pair) = CreatePair();
        var provider = new StubPrivateProfileService(new PrivateProfileDto(owner, "social", DateTimeOffset.UtcNow));
        var repository = new StubTransactionRepository(pair)
        {
            SnapshotResult = new SocialRelationshipSnapshot(pair, SocialRelationshipState.Matched,
                SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow))
        };
        var service = new SocialRelationshipAccessService(provider, repository, new StubEvidenceProvider());
        var status = await service.GetRelationshipStatusAsync(target);
        Assert.False(status.HasActiveMatch);
        Assert.Null(status.MatchId);
    }

    [Fact]
    public async Task GetRelationshipStatusAsync_WhenNoMatch_ConcealsUnilateralInterestsAndReturnsNotMatched()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            SnapshotResult = new SocialRelationshipSnapshot(pair, SocialRelationshipState.PendingUnilateral)
        };
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.GetRelationshipStatusAsync(idB, CancellationToken.None);

        Assert.False(result.HasActiveMatch);
        Assert.Null(result.MatchId);
        Assert.Null(result.MatchedAtUtc);
    }

    [Fact]
    public async Task RevokeMatchAsync_WhenAuthorized_DelegatesToRepository()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RevokeMatchAsync(idB, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.MatchRevoked);
        Assert.True(stubRepo.RevokeMatchCalled);
    }

    [Fact]
    public async Task Operations_WhenCancellationRequested_PropagateCancellation()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair);
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetRelationshipStatusAsync(idB, cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RevokeMatchAsync(idB, cts.Token));
    }

    [Fact]
    public void Results_JsonSerialization_ConcealSensitiveProperties()
    {
        var opResult = SocialRelationshipOperationResult.MatchEstablished(Guid.NewGuid());
        var statusResult = SocialRelationshipStatusResult.Matched(Guid.NewGuid(), DateTime.UtcNow);
        var revokeResult = new SocialRevocationOperationResult(true, true);

        Assert.Equal("{}", JsonSerializer.Serialize(opResult));
        Assert.Equal("{}", JsonSerializer.Serialize(statusResult));
        Assert.Equal("{}", JsonSerializer.Serialize(revokeResult));

        Assert.Equal("SocialRelationshipOperationResult [private]", opResult.ToString());
        Assert.Equal("SocialRelationshipStatusResult [private]", statusResult.ToString());
        Assert.Equal("SocialRevocationOperationResult [private]", revokeResult.ToString());
    }

    [Fact]
    public async Task RecordInterestAsync_WhenTransactionSuccessIsFalse_ReturnsFailedResult()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: false,
                matchCreated: false,
                match: null,
                resultingState: SocialRelationshipState.PendingUnilateral)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task RecordInterestAsync_FailedTransaction_CannotProduceSuccessfulAcknowledgment()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: false,
                matchCreated: false,
                match: null,
                resultingState: SocialRelationshipState.None)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenFailedTransactionContainsMatch_ReturnsFailedResult()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: false,
                matchCreated: true,
                match: match,
                resultingState: SocialRelationshipState.Matched)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenMatchCreatedWithMissingMatch_ReturnsFailedResult()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: true,
                matchCreated: true,
                match: null,
                resultingState: SocialRelationshipState.Matched)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenMatchBelongsToForeignCanonicalPair_ReturnsFailedResult()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var foreignPair = SocialProfilePairKey.Create(Guid.NewGuid(), Guid.NewGuid());
        var foreignMatch = SocialMatchEntity.Create(Guid.NewGuid(), foreignPair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: true,
                matchCreated: true,
                match: foreignMatch,
                resultingState: SocialRelationshipState.Matched)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task GetRelationshipStatusAsync_WhenSnapshotBelongsToForeignPair_ReturnsNotMatched()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var foreignPair = SocialProfilePairKey.Create(Guid.NewGuid(), Guid.NewGuid());
        var match = SocialMatchEntity.Create(Guid.NewGuid(), foreignPair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            SnapshotResult = new SocialRelationshipSnapshot(foreignPair, SocialRelationshipState.Matched, match)
        };
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.GetRelationshipStatusAsync(idB, CancellationToken.None);

        Assert.False(result.HasActiveMatch);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task RecordInterestAsync_WhenInconsistentResultingState_ReturnsFailedResult()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: true,
                matchCreated: true,
                match: match,
                resultingState: SocialRelationshipState.Passed)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchCreated);
    }

    [Fact]
    public async Task RevokeMatchAsync_WhenInvalidRevocationOutcome_ReturnsSuccessFalse()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RevokeResult = new SocialRevocationTransactionResult(
                success: false,
                matchRevoked: true,
                revokedMatchId: Guid.NewGuid(),
                revokedAtUtc: DateTime.UtcNow)
        };
        var stubEvidence = new StubEvidenceProvider();

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RevokeMatchAsync(idB, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.MatchRevoked);
    }

    [Fact]
    public async Task Operations_WhenRepositoryFails_PropagatesException()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair) { ShouldThrow = true };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordInterestAsync(idB, isPositive: true, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetRelationshipStatusAsync(idB, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevokeMatchAsync(idB, CancellationToken.None));
    }

    [Fact]
    public async Task RecordInterestAsync_WhenNegativeDecisionRecorded_DoesNotDiscloseUnilateralDecision()
    {
        var (idA, idB, pair) = CreatePair();
        var profile = new PrivateProfileDto(idA, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubTransactionRepository(pair)
        {
            RecordInterestResult = new SocialInterestTransactionResult(
                success: true,
                matchCreated: false,
                match: null,
                resultingState: SocialRelationshipState.Passed)
        };
        var stubEvidence = new StubEvidenceProvider
        {
            CallerEvidence = new SocialDiscoveryCallerEvidence(idA, 25, isOptedIn: true, isActive: true),
            CandidateEvidence = CreateCandidateEvidence(idB, 26)
        };

        var service = new SocialRelationshipAccessService(stubProfileService, stubRepo, stubEvidence);

        var result = await service.RecordInterestAsync(idB, isPositive: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.MatchCreated);
        Assert.Null(result.MatchId);
        Assert.Equal(SocialRelationshipState.Passed, result.VisibleState);
    }

    [Fact]
    public void Results_NeverExposeCommunicationPermissionsOrTokens()
    {
        var opProps = typeof(SocialRelationshipOperationResult).GetProperties();
        var statusProps = typeof(SocialRelationshipStatusResult).GetProperties();
        var revokeProps = typeof(SocialRevocationOperationResult).GetProperties();

        foreach (var prop in opProps)
        {
            Assert.DoesNotContain("token", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chat", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("room", prop.Name, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var prop in statusProps)
        {
            Assert.DoesNotContain("token", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chat", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("room", prop.Name, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var prop in revokeProps)
        {
            Assert.DoesNotContain("token", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chat", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("room", prop.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
