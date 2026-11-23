using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialInterestRepositoryContractTests
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileC = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime ValidUtc = new(2026, 11, 23, 14, 0, 0, DateTimeKind.Utc);

    #region A. Interface Contract Shape Tests

    [Fact]
    public void Interface_IsInterfaceType()
    {
        Assert.True(typeof(ISocialInterestRepository).IsInterface);
    }

    [Fact]
    public void Interface_DeclaresExactlyFourMethods()
    {
        var methods = typeof(ISocialInterestRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(4, methods.Length);
        Assert.Contains(methods, m => m.Name == "GetDirectionalInterestAsync");
        Assert.Contains(methods, m => m.Name == "HasRecordedInterestAsync");
        Assert.Contains(methods, m => m.Name == "GetPairInterestsAsync");
        Assert.Contains(methods, m => m.Name == "SaveDirectionalInterestAsync");
    }

    [Fact]
    public void Interface_AllMethods_AcceptOptionalCancellationToken()
    {
        var methods = typeof(ISocialInterestRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var lastParam = method.GetParameters().LastOrDefault();
            Assert.NotNull(lastParam);
            Assert.Equal(typeof(CancellationToken), lastParam.ParameterType);
            Assert.True(lastParam.IsOptional);
        }
    }

    [Fact]
    public void Interface_DoesNotExposeProhibitedBulkOrUnscopedMethods()
    {
        var methods = typeof(ISocialInterestRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "GetAll", "ListAll", "FindAll", "QueryAll", "EnumerateAll", "FetchAll" };

        foreach (var method in methods)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interface_DoesNotExposeAuthenticationOrCredentialParameters()
    {
        var methods = typeof(ISocialInterestRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var prohibitedTypes = new[] { "ClaimsPrincipal", "Token", "Credential", "Session", "Password", "Auth" };

        foreach (var method in methods)
        {
            foreach (var param in method.GetParameters())
            {
                if (param.ParameterType == typeof(CancellationToken)) continue;

                foreach (var prohibited in prohibitedTypes)
                {
                    Assert.DoesNotContain(prohibited, param.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(prohibited, param.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    #endregion

    #region B. In-Memory Test Fake Semantics Tests

    [Fact]
    public async Task SaveDirectionalInterestAsync_And_GetDirectionalInterestAsync_RoundTripSuccessfully()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);

        await repo.SaveDirectionalInterestAsync(interest);
        var retrieved = await repo.GetDirectionalInterestAsync(pairKey, ProfileA);

        Assert.NotNull(retrieved);
        Assert.Equal(interest, retrieved);
    }

    [Fact]
    public async Task GetDirectionalInterestAsync_UnrecordedProfile_ReturnsNull()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);

        await repo.SaveDirectionalInterestAsync(interest);
        var result = await repo.GetDirectionalInterestAsync(pairKey, ProfileB);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDirectionalInterestAsync_DifferentPairKey_ReturnsNull()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKey2 = new SocialProfilePairKey(ProfileA, ProfileC);
        var interest = new SocialDirectionalInterest(pairKey1, ProfileA, true, ValidUtc);

        await repo.SaveDirectionalInterestAsync(interest);
        var result = await repo.GetDirectionalInterestAsync(pairKey2, ProfileA);

        Assert.Null(result);
    }

    [Fact]
    public async Task HasRecordedInterestAsync_ReflectsStorageState()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, false, ValidUtc);

        Assert.False(await repo.HasRecordedInterestAsync(pairKey, ProfileA));

        await repo.SaveDirectionalInterestAsync(interest);

        Assert.True(await repo.HasRecordedInterestAsync(pairKey, ProfileA));
        Assert.False(await repo.HasRecordedInterestAsync(pairKey, ProfileB));
    }

    [Fact]
    public async Task GetPairInterestsAsync_ReturnsBothParticipantsDecisions()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, ValidUtc);
        var interestB = new SocialDirectionalInterest(pairKey, pairKey.SecondProfileId, false, ValidUtc);

        await repo.SaveDirectionalInterestAsync(interestA);
        await repo.SaveDirectionalInterestAsync(interestB);

        var (first, second) = await repo.GetPairInterestsAsync(pairKey);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(interestA, first);
        Assert.Equal(interestB, second);
    }

    [Fact]
    public async Task SaveDirectionalInterestAsync_Null_ThrowsArgumentNullException()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        await Assert.ThrowsAsync<ArgumentNullException>(() => repo.SaveDirectionalInterestAsync(null!));
    }

    #endregion

    #region C. Cancellation Tests

    [Fact]
    public async Task Operations_WithCancelledToken_ThrowOperationCanceledException()
    {
        var repo = new InMemorySocialInterestRepositoryFake();
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetDirectionalInterestAsync(pairKey, ProfileA, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.HasRecordedInterestAsync(pairKey, ProfileA, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetPairInterestsAsync(pairKey, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SaveDirectionalInterestAsync(interest, cts.Token));
    }

    #endregion

    #region Nested Test Fake

    private sealed class InMemorySocialInterestRepositoryFake : ISocialInterestRepository
    {
        private readonly Dictionary<(SocialProfilePairKey PairKey, Guid ActingProfileId), SocialDirectionalInterest> _storage = new();

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pairKey is null || actingProfileId == Guid.Empty)
            {
                return Task.FromResult<SocialDirectionalInterest?>(null);
            }

            _storage.TryGetValue((pairKey, actingProfileId), out var interest);
            return Task.FromResult(interest);
        }

        public Task<bool> HasRecordedInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pairKey is null || actingProfileId == Guid.Empty)
            {
                return Task.FromResult(false);
            }

            var exists = _storage.ContainsKey((pairKey, actingProfileId));
            return Task.FromResult(exists);
        }

        public Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pairKey is null)
            {
                return Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((null, null));
            }

            _storage.TryGetValue((pairKey, pairKey.FirstProfileId), out var first);
            _storage.TryGetValue((pairKey, pairKey.SecondProfileId), out var second);

            return Task.FromResult((first, second));
        }

        public Task SaveDirectionalInterestAsync(
            SocialDirectionalInterest interest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(interest);

            _storage[(interest.PairKey, interest.ActingProfileId)] = interest;
            return Task.CompletedTask;
        }
    }

    #endregion
}
