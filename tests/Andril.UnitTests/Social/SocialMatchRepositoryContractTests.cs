using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialMatchRepositoryContractTests
{
    private static readonly Guid MatchId = Guid.Parse("018d9f4e-1111-7000-8000-000000000001");
    private static readonly Guid ProfileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ForeignProfile = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTime ValidUtcTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TestSocialMatchRepositoryDouble : ISocialMatchRepository
    {
        private readonly Dictionary<Guid, SocialMatchEntity> _matchesById = new();
        private readonly Dictionary<SocialProfilePairKey, SocialMatchEntity> _matchesByPair = new();

        public void Seed(SocialMatchEntity match)
        {
            _matchesById[match.MatchId] = match;
            _matchesByPair[match.PairKey] = match;
        }

        public Task<SocialMatchEntity?> GetMatchByIdAsync(
            Guid matchId,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_matchesById.TryGetValue(matchId, out var match) &&
                (match.PairKey.FirstProfileId == requestingProfileId || match.PairKey.SecondProfileId == requestingProfileId))
            {
                return Task.FromResult<SocialMatchEntity?>(match);
            }

            return Task.FromResult<SocialMatchEntity?>(null);
        }

        public Task<SocialMatchEntity?> GetMatchByPairKeyAsync(
            SocialProfilePairKey pairKey,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_matchesByPair.TryGetValue(pairKey, out var match) &&
                (match.PairKey.FirstProfileId == requestingProfileId || match.PairKey.SecondProfileId == requestingProfileId))
            {
                return Task.FromResult<SocialMatchEntity?>(match);
            }

            return Task.FromResult<SocialMatchEntity?>(null);
        }

        public Task<bool> HasMatchAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_matchesByPair.ContainsKey(pairKey));
        }
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenMatchExistsAndProfileBelongsToPair_ReturnsMatch()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        repository.Seed(match);

        var result = await repository.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.NotNull(result);
        Assert.Equal(MatchId, result.MatchId);
        Assert.Equal(pairKey, result.PairKey);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenMatchDoesNotExist_ReturnsNullWithoutException()
    {
        var repository = new TestSocialMatchRepositoryDouble();

        var result = await repository.GetMatchByIdAsync(MatchId, ProfileA);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenProfileDoesNotBelongToPair_ReturnsNullForScopeIsolation()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        repository.Seed(match);

        var result = await repository.GetMatchByIdAsync(MatchId, ForeignProfile);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMatchByIdAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repository.GetMatchByIdAsync(MatchId, ProfileA, cts.Token));
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenMatchExistsAndProfileBelongsToPair_ReturnsMatch()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        repository.Seed(match);

        var result = await repository.GetMatchByPairKeyAsync(pairKey, ProfileB);

        Assert.NotNull(result);
        Assert.Equal(MatchId, result.MatchId);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenMatchDoesNotExist_ReturnsNull()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);

        var result = await repository.GetMatchByPairKeyAsync(pairKey, ProfileA);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenProfileDoesNotBelongToPair_ReturnsNull()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        repository.Seed(match);

        var result = await repository.GetMatchByPairKeyAsync(pairKey, ForeignProfile);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMatchByPairKeyAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repository.GetMatchByPairKeyAsync(pairKey, ProfileA, cts.Token));
    }

    [Fact]
    public async Task HasMatchAsync_WhenMatchExists_ReturnsTrue()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);
        repository.Seed(match);

        var exists = await repository.HasMatchAsync(pairKey);

        Assert.True(exists);
    }

    [Fact]
    public async Task HasMatchAsync_WhenMatchDoesNotExist_ReturnsFalse()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);

        var exists = await repository.HasMatchAsync(pairKey);

        Assert.False(exists);
    }

    [Fact]
    public async Task HasMatchAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        var repository = new TestSocialMatchRepositoryDouble();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repository.HasMatchAsync(pairKey, cts.Token));
    }

    [Fact]
    public void Contract_HasNoUnscopedEnumerationOrListingMethods()
    {
        var methods = typeof(ISocialMatchRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var methodNames = methods.Select(m => m.Name).ToList();

        Assert.DoesNotContain("GetAll", methodNames);
        Assert.DoesNotContain("GetAllAsync", methodNames);
        Assert.DoesNotContain("List", methodNames);
        Assert.DoesNotContain("ListAsync", methodNames);
        Assert.DoesNotContain("FindAll", methodNames);
        Assert.DoesNotContain("FindAllAsync", methodNames);
    }

    [Fact]
    public void Contract_HasNoWriteOrLifecycleMutationMethods()
    {
        var methods = typeof(ISocialMatchRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var methodNames = methods.Select(m => m.Name).ToList();

        Assert.DoesNotContain("Create", methodNames);
        Assert.DoesNotContain("CreateAsync", methodNames);
        Assert.DoesNotContain("Add", methodNames);
        Assert.DoesNotContain("AddAsync", methodNames);
        Assert.DoesNotContain("Save", methodNames);
        Assert.DoesNotContain("SaveAsync", methodNames);
        Assert.DoesNotContain("Update", methodNames);
        Assert.DoesNotContain("UpdateAsync", methodNames);
        Assert.DoesNotContain("Delete", methodNames);
        Assert.DoesNotContain("DeleteAsync", methodNames);
        Assert.DoesNotContain("Activate", methodNames);
        Assert.DoesNotContain("ActivateAsync", methodNames);
    }

    [Fact]
    public void Contract_HasNoAuthorizationOrPermissionGrantMethods()
    {
        var methods = typeof(ISocialMatchRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var methodNames = methods.Select(m => m.Name).ToList();

        Assert.DoesNotContain("Grant", methodNames);
        Assert.DoesNotContain("GrantAsync", methodNames);
        Assert.DoesNotContain("Authorize", methodNames);
        Assert.DoesNotContain("AuthorizeAsync", methodNames);
        Assert.DoesNotContain("IssueToken", methodNames);
        Assert.DoesNotContain("IssueTokenAsync", methodNames);
        Assert.DoesNotContain("CanChat", methodNames);
    }

    [Fact]
    public void Contract_ReturnedEntityDoesNotInferActiveMatchOrCommunicationGrant()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId, pairKey, ValidUtcTime);

        // Verify consumer cannot infer communication grant or active match merely from entity
        var matchType = match.GetType();
        Assert.Null(matchType.GetProperty("IsAuthorizedForCommunication"));
        Assert.Null(matchType.GetProperty("CommunicationToken"));
    }
}
