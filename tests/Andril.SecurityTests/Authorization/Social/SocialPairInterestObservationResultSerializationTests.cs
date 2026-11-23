using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Security tests verifying that <see cref="SocialPairInterestObservationResult"/> prevents
/// accidental transport serialization of private profile identities, directional interest decisions,
/// mutual-match status, and internal observation diagnostics while preserving internal C# access.
/// </summary>
public sealed class SocialPairInterestObservationResultSerializationTests
{
    private static readonly Guid ProfileIdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileIdB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ForeignProfileId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime DecisionTimestampUtc = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions StringEnumJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static SocialProfilePairKey CanonicalPair => new(ProfileIdA, ProfileIdB);

    private sealed class TestInterestRepositoryDouble : ISocialInterestRepository
    {
        public Func<SocialProfilePairKey, CancellationToken, Task<(SocialDirectionalInterest?, SocialDirectionalInterest?)>>? Handler { get; set; }

        public Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Handler is not null
                ? Handler(pairKey, cancellationToken)
                : Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((null, null));
        }

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> HasRecordedInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveDirectionalInterestAsync(SocialDirectionalInterest interest, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static void AssertSerializedPayloadConcealsAllPrivateData(SocialPairInterestObservationResult result)
    {
        var defaultJson = JsonSerializer.Serialize(result);
        var enumStringJson = JsonSerializer.Serialize(result, StringEnumJsonOptions);

        Assert.Equal("{}", defaultJson);
        Assert.Equal("{}", enumStringJson);

        foreach (var json in new[] { defaultJson, enumStringJson })
        {
            Assert.DoesNotContain(ProfileIdA.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileIdB.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ForeignProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationResult.PairKey), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationResult.ReciprocalStatus), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationResult.ObservationStatus), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationResult.IsValid), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationResult.IsMutualMatch), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialReciprocalInterestStatus.MutualInterestObserved), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialReciprocalInterestStatus.NotMutual), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialReciprocalInterestStatus.Indeterminate), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationStatus.ScopeViolation), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(SocialPairInterestObservationStatus.InvalidIdentity), json, StringComparison.OrdinalIgnoreCase);
        }

        var diagnostic = result.ToString();
        Assert.Equal("SocialPairInterestObservationResult [private]", diagnostic);
        Assert.DoesNotContain(ProfileIdA.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileIdB.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ForeignProfileId.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Serialize_MutualInterest_ConcealsIdentifiersAndMutualMatchStatusWhilePreservingInternalAccess()
    {
        var pair = CanonicalPair;
        var first = new SocialDirectionalInterest(pair, pair.FirstProfileId, true, DecisionTimestampUtc);
        var second = new SocialDirectionalInterest(pair, pair.SecondProfileId, true, DecisionTimestampUtc);

        var repo = new TestInterestRepositoryDouble
        {
            Handler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((first, second))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pair);

        Assert.True(result.IsValid);
        Assert.True(result.IsMutualMatch);
        Assert.Equal(pair, result.PairKey);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public async Task Serialize_OneSidedInterest_ConcealsPrivateDecisionAndRemainsNonMutual()
    {
        var pair = CanonicalPair;
        var first = new SocialDirectionalInterest(pair, pair.FirstProfileId, true, DecisionTimestampUtc);

        var repo = new TestInterestRepositoryDouble
        {
            Handler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((first, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public async Task Serialize_NegativeInterest_ConcealsPassDecision()
    {
        var pair = CanonicalPair;
        var first = new SocialDirectionalInterest(pair, pair.FirstProfileId, true, DecisionTimestampUtc);
        var second = new SocialDirectionalInterest(pair, pair.SecondProfileId, false, DecisionTimestampUtc);

        var repo = new TestInterestRepositoryDouble
        {
            Handler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((first, second))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public async Task Serialize_NoRecordedInterest_ConcealsObservationState()
    {
        var pair = CanonicalPair;
        var repo = new TestInterestRepositoryDouble();
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(pair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public async Task Serialize_ForeignPairEvidenceScopeViolation_ConcealsViolationAndIdentifiers()
    {
        var requestedPair = CanonicalPair;
        var foreignPair = new SocialProfilePairKey(ProfileIdA, ForeignProfileId);
        var foreignInterest = new SocialDirectionalInterest(foreignPair, ProfileIdA, true, DecisionTimestampUtc);

        var repo = new TestInterestRepositoryDouble
        {
            Handler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((foreignInterest, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(requestedPair);

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public void Serialize_InvalidIdentityResult_ConcealsFailureState()
    {
        var result = SocialPairInterestObservationResult.InvalidIdentity();

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Null(result.PairKey);
        Assert.Equal(SocialPairInterestObservationStatus.InvalidIdentity, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);

        AssertSerializedPayloadConcealsAllPrivateData(result);
    }

    [Fact]
    public void ResultType_DoesNotExposeImplicitCommunicationAuthorizationMembers()
    {
        var propertyNames = typeof(SocialPairInterestObservationResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Chat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Conversation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Message", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
    }
}
