using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Matching;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialDiscoveryAccessServiceTests
{
    #region 1. Constructor and Parameter Validation

    [Fact]
    public void Constructor_NullProfileService_ThrowsArgumentNullException()
    {
        var provider = new StubEvidenceProvider();
        Assert.Throws<ArgumentNullException>(() => new SocialDiscoveryAccessService(null!, provider));
    }

    [Fact]
    public void Constructor_NullEvidenceProvider_ThrowsArgumentNullException()
    {
        var profileService = new StubPrivateProfileService(Guid.NewGuid());
        Assert.Throws<ArgumentNullException>(() => new SocialDiscoveryAccessService(profileService, null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task DiscoverCandidatesAsync_NegativeRotationOffset_ThrowsArgumentOutOfRangeException(int offset)
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DiscoverCandidatesAsync(rotationOffset: offset, limit: 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public async Task DiscoverCandidatesAsync_ZeroOrNegativeLimit_ThrowsArgumentOutOfRangeException(int limit)
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DiscoverCandidatesAsync(rotationOffset: 0, limit: limit));
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DiscoverCandidatesAsync(rotationOffset: 0, limit: 20, cancellationToken: cts.Token));
    }

    #endregion

    #region 2. Authentication and Caller Scope Verification

    [Fact]
    public async Task DiscoverCandidatesAsync_UnauthenticatedOrUnauthorizedCaller_ThrowsProfileAccessDeniedException()
    {
        var profileService = new ThrowingProfileService(new ProfileAccessDeniedException());
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.DiscoverCandidatesAsync());
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_WrongModule_ThrowsProfileAccessDeniedException()
    {
        var callerId = Guid.NewGuid();
        var wrongModuleProfile = new PrivateProfileDto(callerId, "hobbies", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(wrongModuleProfile);
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.DiscoverCandidatesAsync());
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_EmptyProfileId_ThrowsProfileAccessDeniedException()
    {
        var emptyProfile = new PrivateProfileDto(Guid.Empty, "social", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(emptyProfile);
        var provider = new StubEvidenceProvider();
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.DiscoverCandidatesAsync());
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerBundleProfileIdMismatch_ThrowsProfileAccessDeniedException()
    {
        var callerId = Guid.NewGuid();
        var mismatchedId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(mismatchedId);
        var callerBundle = new SocialDiscoveryCallerBundle(
            callerEvidence,
            CreateSnapshot(mismatchedId),
            CreateSnapshot(mismatchedId));

        var provider = new StubEvidenceProvider { CallerBundle = callerBundle };
        var service = new SocialDiscoveryAccessService(profileService, provider);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.DiscoverCandidatesAsync());
    }

    #endregion

    #region 3. Caller Eligibility and Fail-Closed Safeguards

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerBundleNull_FailsClosedReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);
        var provider = new StubEvidenceProvider { CallerBundle = null };
        var service = new SocialDiscoveryAccessService(profileService, provider);

        var result = await service.DiscoverCandidatesAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerUnderage_FailsClosedReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, age: 17);
        var candidate = CreateProfile(age: 25);
        var candidateItem = CreateCandidateItem(candidate);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[] { candidateItem }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerNotOptedIn_FailsClosedReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, isOptedIn: false);
        var candidate = CreateProfile(age: 25);
        var candidateItem = CreateCandidateItem(candidate);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[] { candidateItem }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerNotActive_FailsClosedReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, isActive: false);
        var candidate = CreateProfile(age: 25);
        var candidateItem = CreateCandidateItem(candidate);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[] { candidateItem }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerMissingAuthoritativeEvidence_FailsClosedReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, hasAuthoritativeEvidence: false);
        var candidate = CreateProfile(age: 25);
        var candidateItem = CreateCandidateItem(candidate);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[] { candidateItem }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateBatchEmpty_ReturnsEmpty()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = Array.Empty<SocialAuthoritativeCandidateItem>()
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        Assert.Empty(result);
    }

    #endregion

    #region 4. Candidate Privacy, Visibility, and Bidirectional Blocking

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateUnderage_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, age: 25);
        var underageCand = CreateProfile(age: 17);
        var adultCand = CreateProfile(age: 24);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(underageCand),
                CreateCandidateItem(adultCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(adultCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateNotOptedIn_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var optedOutCand = CreateProfile(isOptedIn: false);
        var optedInCand = CreateProfile(isOptedIn: true);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(optedOutCand),
                CreateCandidateItem(optedInCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(optedInCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateNotActive_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var inactiveCand = CreateProfile(isActive: false);
        var activeCand = CreateProfile(isActive: true);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(inactiveCand),
                CreateCandidateItem(activeCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(activeCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateHidden_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var hiddenCand = CreateProfile(isDiscoverable: false);
        var visibleCand = CreateProfile(isDiscoverable: true);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(hiddenCand),
                CreateCandidateItem(visibleCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(visibleCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateMissingAuthoritativeEvidence_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var missingEvidenceCand = CreateProfile(hasAuthoritativeEvidence: false);
        var validCand = CreateProfile(hasAuthoritativeEvidence: true);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(missingEvidenceCand),
                CreateCandidateItem(validCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(validCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CallerBlockedCandidate_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var blockedCand = CreateProfile();
        var unblockedCand = CreateProfile();

        var profileService = new StubPrivateProfileService(callerId);
        var callerEvidence = CreateProfile(callerId, blockedProfileIds: new[] { blockedCand.ProfileId });

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(blockedCand),
                CreateCandidateItem(unblockedCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(unblockedCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_CandidateBlockedCaller_ExcludesCandidate()
    {
        var callerId = Guid.NewGuid();
        var candidateBlockingCaller = CreateProfile(blockedProfileIds: new[] { callerId });
        var normalCandidate = CreateProfile();

        var profileService = new StubPrivateProfileService(callerId);
        var callerEvidence = CreateProfile(callerId);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(candidateBlockingCaller),
                CreateCandidateItem(normalCandidate)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(normalCandidate.ProfileId, card.CandidateProfileId);
    }

    #endregion

    #region 5. Bounded Processing, Contradictory Duplicates, and Safe Projections

    [Fact]
    public async Task DiscoverCandidatesAsync_ContradictoryDuplicateCandidates_RejectsContradictoryDuplicates()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var candId = Guid.NewGuid();

        // Contradictory duplicate: same profileId with differing age/gender evidence
        var candA = CreateProfile(candId, age: 24, demographicGender: "male");
        var candB = CreateProfile(candId, age: 28, demographicGender: "female");
        var legitimateCand = CreateProfile(age: 26);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(candA),
                CreateCandidateItem(candB),
                CreateCandidateItem(legitimateCand)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(legitimateCand.ProfileId, card.CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_BoundedProcessing_EnforcesBatchLimit()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var candidates = Enumerable.Range(1, 5)
            .Select(_ => CreateCandidateItem(CreateProfile()))
            .ToArray();

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = candidates
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync(rotationOffset: 0, limit: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_DeterministicRotation_RotatesCandidatesDeterministically()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId);
        var cand1 = CreateProfile();
        var cand2 = CreateProfile();

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[]
            {
                CreateCandidateItem(cand1),
                CreateCandidateItem(cand2)
            }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var offset0 = await service.DiscoverCandidatesAsync(rotationOffset: 0, limit: 10);
        var offset1 = await service.DiscoverCandidatesAsync(rotationOffset: 1, limit: 10);

        Assert.Equal(2, offset0.Count);
        Assert.Equal(2, offset1.Count);
        // Rotation should deterministically shift items
        Assert.Equal(offset0[0].CandidateProfileId, offset1[1].CandidateProfileId);
        Assert.Equal(offset0[1].CandidateProfileId, offset1[0].CandidateProfileId);
    }

    [Fact]
    public async Task DiscoverCandidatesAsync_EligibleCandidate_ReturnsPrivacySafeProjectedCards()
    {
        var callerId = Guid.NewGuid();
        var profileService = new StubPrivateProfileService(callerId);

        var callerEvidence = CreateProfile(callerId, languages: new[] { "English", "French" }, goals: new[] { "Friendship" });
        var candidate = CreateProfile(age: 26, languages: new[] { "English" }, goals: new[] { "Friendship" });
        var candidateItem = CreateCandidateItem(candidate, displayName: "VerifiedCandidate", distanceKm: 12);

        var provider = new StubEvidenceProvider
        {
            CallerBundle = new SocialDiscoveryCallerBundle(
                callerEvidence,
                CreateSnapshot(callerId),
                CreateSnapshot(callerId)),
            CandidateBatch = new[] { candidateItem }
        };

        var service = new SocialDiscoveryAccessService(profileService, provider);
        var result = await service.DiscoverCandidatesAsync();

        var card = Assert.Single(result);
        Assert.Equal(candidate.ProfileId, card.CandidateProfileId);
        Assert.Equal("VerifiedCandidate", card.DisplayName);
        Assert.Equal(26, card.Age);
        Assert.Equal(12, card.ApproximateDistanceKm);
        Assert.Contains("English", card.SharedLanguages);
        Assert.Contains("Friendship", card.SharedConnectionGoals);
    }

    #endregion

    #region Helper Stubs and Factories

    private sealed class StubPrivateProfileService : IPrivateProfileService
    {
        private readonly PrivateProfileDto _profile;

        public StubPrivateProfileService(Guid profileId)
        {
            _profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        }

        public StubPrivateProfileService(PrivateProfileDto profile)
        {
            _profile = profile;
        }

        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action(_profile);
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class ThrowingProfileService(Exception exception) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken) =>
            Task.FromException<T>(exception);

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromException<PrivateProfileDto>(exception);

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromException<PrivateProfileDto>(exception);
    }

    private sealed class StubEvidenceProvider : ISocialDiscoveryEvidenceProvider
    {
        public SocialDiscoveryCallerBundle? CallerBundle { get; set; }
        public IReadOnlyList<SocialAuthoritativeCandidateItem> CandidateBatch { get; set; } = Array.Empty<SocialAuthoritativeCandidateItem>();

        public Task<SocialDiscoveryCallerBundle?> GetCallerBundleAsync(Guid callerProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CallerBundle);

        public Task<IReadOnlyList<SocialAuthoritativeCandidateItem>> GetCandidateBatchAsync(Guid callerProfileId, int maxCandidateBatchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(CandidateBatch);
    }

    private static SocialEvidenceRevisionSnapshot CreateSnapshot(
        Guid profileId,
        string policyVersion = SocialCompatibilityScoringEngine.PolicyVersion,
        long questionnaireRevision = 1,
        long partnerPreferencesRevision = 1,
        long demographicsRevision = 1,
        bool isProfileActive = true,
        bool isAccountRestricted = false)
    {
        return new SocialEvidenceRevisionSnapshot(
            profileId,
            policyVersion: policyVersion,
            questionnaireRevision: questionnaireRevision,
            partnerPreferencesRevision: partnerPreferencesRevision,
            demographicsRevision: demographicsRevision,
            isProfileActive: isProfileActive,
            isAccountRestricted: isAccountRestricted);
    }

    private static SocialAuthoritativeCandidateItem CreateCandidateItem(
        SocialScoringProfileEvidence evidence,
        SocialEvidenceRevisionSnapshot? cached = null,
        SocialEvidenceRevisionSnapshot? committed = null,
        string displayName = "CandidateUser",
        int? distanceKm = 10)
    {
        var c = cached ?? CreateSnapshot(evidence.ProfileId);
        var com = committed ?? CreateSnapshot(evidence.ProfileId);
        return new SocialAuthoritativeCandidateItem(evidence, c, com, displayName, distanceKm);
    }

    private static SocialScoringProfileEvidence CreateProfile(
        Guid? profileId = null,
        int age = 25,
        string? demographicGender = "female",
        SocialGenderPreference genderPreference = SocialGenderPreference.All,
        int minAge = 18,
        int maxAge = 99,
        int maxDistanceKm = 100,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? goals = null,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool hasAuthoritativeEvidence = true,
        IEnumerable<Guid>? blockedProfileIds = null)
    {
        var rangeFilters = new SocialExploreRangeFilters(
            minAge: minAge,
            maxAge: maxAge,
            minDistanceKm: 0,
            maxDistanceKm: maxDistanceKm);

        var prefs = new SocialExplorePreferences(
            rangeFilters,
            genderPreference,
            languages: languages ?? new[] { "English" },
            connectionGoals: goals ?? new[] { "Friendship" });

        return new SocialScoringProfileEvidence(
            profileId ?? Guid.NewGuid(),
            age,
            demographicGender,
            prefs,
            confirmedLanguages: languages ?? new[] { "English" },
            confirmedConnectionGoals: goals ?? new[] { "Friendship" },
            isOptedIn: isOptedIn,
            isActive: isActive,
            isDiscoverable: isDiscoverable,
            hasAuthoritativeEvidence: hasAuthoritativeEvidence,
            blockedProfileIds: blockedProfileIds);
    }

    #endregion
}
