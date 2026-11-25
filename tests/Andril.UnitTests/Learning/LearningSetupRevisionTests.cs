using System.Text.Json;
using Andril.Learning.Entities;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningSetupRevisionTests
{
    private static readonly Guid Generation = Guid.Parse("12345678-abcd-4321-abcd-123456789abc");

    [Theory]
    [InlineData(0L)] [InlineData(1L)] [InlineData(long.MaxValue)]
    public void CanonicalTokenRoundTripsPersistedPairIncludingVersionBoundary(long version)
    {
        var value = LearningSetupRevision.FromPersisted(Generation, version);
        var token = value.ToOpaqueToken(); Assert.Equal(53, token.Length);
        Assert.True(LearningSetupRevision.TryParse(token, out var parsed));
        Assert.Equal(Generation, parsed!.GenerationId); Assert.Equal(version, parsed.Version);
        Assert.Equal(token, parsed.ToOpaqueToken());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("storage-v1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123")]
    [InlineData("lr1.00000000000000000000000000000000.0000000000000001")]
    [InlineData("lr1.12345678abcd4321abcd123456789abc.8000000000000000")]
    [InlineData("lr1.12345678abcd4321abcd123456789abc.ffffffffffffffff")]
    [InlineData("lr1.12345678abcd4321abcd123456789abc.000000000000000g")]
    [InlineData("lr2.12345678abcd4321abcd123456789abc.0000000000000001")]
    [InlineData("lr1.12345678ABCD4321ABCD123456789ABC.0000000000000001")]
    [InlineData("lr1.12345678-abcd-4321-abcd-123456789abc.0000000000000001")]
    public void MissingMalformedLegacyAndOverflowTokensAreRejectedWithoutPartialValue(string? token)
    {
        Assert.False(LearningSetupRevision.TryParse(token, out var value)); Assert.Null(value);
    }

    [Fact]
    public void BackendInputsCannotRepresentEmptyGenerationOrNegativeVersion()
    {
        Assert.Throws<ArgumentException>(() => LearningSetupRevision.FromPersisted(Guid.Empty, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LearningSetupRevision.FromPersisted(Generation, -1));
    }

    [Fact]
    public void DifferentGenerationsOrVersionsHaveDifferentOpaqueTokens()
    {
        var first = LearningSetupRevision.FromPersisted(Generation, 1).ToOpaqueToken();
        Assert.NotEqual(first, LearningSetupRevision.FromPersisted(Generation, 2).ToOpaqueToken());
        Assert.NotEqual(first, LearningSetupRevision.FromPersisted(Guid.NewGuid(), 1).ToOpaqueToken());
        Assert.False(LearningSetupRevision.TryParse(" " + first, out _));
        Assert.False(LearningSetupRevision.TryParse(first + " ", out _));
        // Representation tests do not establish durable allocation, monotonicity or ABA protection.
    }

    [Fact]
    public void DefaultJsonAndDiagnosticsDoNotLeakToken()
    {
        var value = LearningSetupRevision.FromPersisted(Generation, long.MaxValue);
        Assert.Equal("{}", JsonSerializer.Serialize(value));
        Assert.DoesNotContain(Generation.ToString("N"), value.ToString()); Assert.DoesNotContain(value.ToOpaqueToken(), value.ToString());
        Assert.All(typeof(LearningSetupRevision).GetProperties(), p => Assert.Null(p.SetMethod));
    }
}
