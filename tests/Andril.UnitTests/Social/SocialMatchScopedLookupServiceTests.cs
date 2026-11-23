using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Andril.Social.Services;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialMatchScopedLookupServiceTests
{
    private static readonly Guid MatchId = Guid.Parse("018d9f4e-1111-7000-8000-000000000001");
    private static readonly Guid OtherMatchId = Guid.Parse("018d9f4e-2222-7000-8000-000000000002");
    private static readonly Guid ProfileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ForeignProfile = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTime ValidUtcTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TestSocialMatchRepository : ISocialMatchRepository
    {
        public SocialMatchEntity? NextMatchById { get; set; }
        public SocialMatchEntity? NextMatchByPair { get; set; }
        public bool ShouldThrow { get; set; }

        public Task<SocialMatchEntity?> GetMatchByIdAsync(
            Guid matchId,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Simulated repository failure.");
            return Task.FromResult(NextMatchById);
        }

        public Task<SocialMatchEntity?> GetMatchByPairKeyAsync(
            SocialProfilePairKey pairKey,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Simulated repository failure.");
            return Task.FromResult(NextMatchByPair);
        }

        public Task<bool> HasMatchAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Simulated repository failure.");
            return Task.FromResult(NextMatchByPair is not null);
        }
    }

    [Fact]
    public async Task GetMatchByIdAsync_WithValidMatchAndAuthorizedProfile_ReturnsSuccess()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchById = match };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.Found, result.Status);
        Assert.True(result.IsFound);
        Assert.False(result.IsNotFound);
        Assert.NotNull(result.Match);
        Assert.Equal(MatchId, result.Match.MatchId);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WithReversedPairConstruction_ReturnsSuccess()
    {
        var pairKey = new SocialProfilePairKey(ProfileB, ProfileA);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchById = match };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.Found, result.Status);
        Assert.True(result.IsFound);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenRepositoryReturnsWrongMatchId_ReturnsMatchIdMismatch()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var wrongMatch = new SocialMatchEntity(OtherMatchId, pairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchById = wrongMatch };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.MatchIdMismatch, result.Status);
        Assert.False(result.IsFound);
        Assert.Null(result.Match);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenRequestingProfileNotInPair_ReturnsProfileIdMismatch()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchById = match };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ForeignProfile);

        Assert.Equal(SocialMatchLookupStatus.ProfileIdMismatch, result.Status);
        Assert.False(result.IsFound);
        Assert.Null(result.Match);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("018d9f4e-1111-7000-8000-000000000001", "00000000-0000-0000-0000-000000000000")]
    public async Task GetMatchByIdAsync_WithEmptyIdentities_ReturnsInvalidIdentity(string matchIdStr, string profileIdStr)
    {
        var repo = new TestSocialMatchRepository();
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(Guid.Parse(matchIdStr), Guid.Parse(profileIdStr));

        Assert.Equal(SocialMatchLookupStatus.InvalidIdentity, result.Status);
        Assert.False(result.IsFound);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenRepositoryReturnsNull_ReturnsNotFound()
    {
        var repo = new TestSocialMatchRepository { NextMatchById = null };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.NotFound, result.Status);
        Assert.True(result.IsNotFound);
        Assert.Null(result.Match);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenRepositoryThrows_ReturnsDependencyFailure()
    {
        var repo = new TestSocialMatchRepository { ShouldThrow = true };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.DependencyFailure, result.Status);
        Assert.False(result.IsFound);
        Assert.Null(result.Match);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenCancellationRequested_PropagatesCancellation()
    {
        var repo = new TestSocialMatchRepository();
        var service = new SocialMatchScopedLookupService(repo);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetMatchByIdAsync(MatchId, ProfileA, cts.Token));
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WithValidPairAndAuthorizedProfile_ReturnsSuccess()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchByPair = match };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByPairKeyAsync(pairKey, ProfileB);

        Assert.Equal(SocialMatchLookupStatus.Found, result.Status);
        Assert.True(result.IsFound);
        Assert.NotNull(result.Match);
        Assert.Equal(MatchId, result.Match.MatchId);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WithNullPairKeyOrEmptyProfile_ReturnsInvalidIdentity()
    {
        var repo = new TestSocialMatchRepository();
        var service = new SocialMatchScopedLookupService(repo);

        var nullPairResult = await service.GetMatchByPairKeyAsync(null!, ProfileA);
        var emptyProfileResult = await service.GetMatchByPairKeyAsync(new SocialProfilePairKey(ProfileA, ProfileB), Guid.Empty);

        Assert.Equal(SocialMatchLookupStatus.InvalidIdentity, nullPairResult.Status);
        Assert.Equal(SocialMatchLookupStatus.InvalidIdentity, emptyProfileResult.Status);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenRequestingProfileNotInPairKey_ReturnsProfileIdMismatch()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var repo = new TestSocialMatchRepository();
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByPairKeyAsync(pairKey, ForeignProfile);

        Assert.Equal(SocialMatchLookupStatus.ProfileIdMismatch, result.Status);
        Assert.False(result.IsFound);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenRepositoryReturnsForeignPair_ReturnsProfileIdMismatch()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var foreignPairKey = new SocialProfilePairKey(ProfileA, ForeignProfile);
        var foreignMatch = new SocialMatchEntity(MatchId, foreignPairKey, ValidUtcTime);
        var repo = new TestSocialMatchRepository { NextMatchByPair = foreignMatch };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByPairKeyAsync(pairKey, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.ProfileIdMismatch, result.Status);
        Assert.False(result.IsFound);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenRepositoryReturnsNull_ReturnsNotFound()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var repo = new TestSocialMatchRepository { NextMatchByPair = null };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByPairKeyAsync(pairKey, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.NotFound, result.Status);
        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenRepositoryThrows_ReturnsDependencyFailure()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var repo = new TestSocialMatchRepository { ShouldThrow = true };
        var service = new SocialMatchScopedLookupService(repo);

        var result = await service.GetMatchByPairKeyAsync(pairKey, ProfileA);

        Assert.Equal(SocialMatchLookupStatus.DependencyFailure, result.Status);
        Assert.False(result.IsFound);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenCancellationRequested_PropagatesCancellation()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var repo = new TestSocialMatchRepository();
        var service = new SocialMatchScopedLookupService(repo);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetMatchByPairKeyAsync(pairKey, ProfileA, cts.Token));
    }

    [Fact]
    public void Result_JsonSerialization_ConcealsConfidentialProperties()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var result = SocialMatchLookupResult.Success(match);

        var json = JsonSerializer.Serialize(result);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(MatchId.ToString(), json);
        Assert.DoesNotContain(ProfileA.ToString(), json);
        Assert.DoesNotContain(ProfileB.ToString(), json);
    }

    [Fact]
    public void Result_ToString_ReturnsSafeDiagnosticRepresentation()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        var result = SocialMatchLookupResult.Success(match);

        var repr = result.ToString();

        Assert.Equal("SocialMatchLookupResult [Found]", repr);
        Assert.DoesNotContain(MatchId.ToString(), repr);
        Assert.DoesNotContain(ProfileA.ToString(), repr);
    }

    [Fact]
    public void BoundaryRestrictions_ServiceDoesNotInferActiveMatchOrGrantChat()
    {
        var type = typeof(SocialMatchScopedLookupService);

        Assert.Null(type.GetMethod("AuthorizeChat"));
        Assert.Null(type.GetMethod("GrantCall"));
        Assert.Null(type.GetMethod("IssueCommunicationEvidence"));
        Assert.Null(type.GetProperty("IsActiveMatch"));
    }
}
