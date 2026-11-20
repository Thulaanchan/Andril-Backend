using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionPhotoEvidenceReaderTests
{
    private static readonly Guid ProfileId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid AttractionId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SessionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid OtherProfileId = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid OtherSessionId = new("55555555-5555-4555-8555-555555555555");
    private const string PhotoKey = "private/hobbies/map/capture/photo.jpg";
    private readonly IMapContributionPhotoEvidenceReader _reader = new DenyAllMapContributionPhotoEvidenceReader();

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task VerifyAsync_DeniesAnEmptyIdentifier(bool hasProfile, bool hasAttraction, bool hasSession)
    {
        var result = await _reader.VerifyAsync(
            hasProfile ? ProfileId : Guid.Empty,
            hasAttraction ? AttractionId : Guid.Empty,
            hasSession ? SessionId : Guid.Empty,
            PhotoKey);

        Assert.Equal(MapContributionPhotoVerificationResult.Denied, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("https://example.invalid/storage/public/photo.jpg")]
    [InlineData(PhotoKey)]
    public async Task VerifyAsync_DeniesPhotoClaimsWithoutAuthoritativeEvidence(string? photoKey)
    {
        var result = await _reader.VerifyAsync(ProfileId, AttractionId, SessionId, photoKey);

        Assert.Equal(MapContributionPhotoVerificationResult.Denied, result);
    }

    [Fact]
    public async Task VerifyAsync_RepeatedRequestsAndDifferentScopesRemainDenied()
    {
        foreach (var (profile, session) in new[]
        {
            (ProfileId, SessionId), (OtherProfileId, SessionId),
            (ProfileId, OtherSessionId), (OtherProfileId, OtherSessionId), (ProfileId, SessionId)
        })
        {
            var result = await _reader.VerifyAsync(profile, AttractionId, session, PhotoKey);
            Assert.Equal(MapContributionPhotoVerificationResult.Denied, result);
        }
    }

    [Fact]
    public async Task VerifyAsync_HonorsPreCancellation()
    {
        var token = new CancellationToken(canceled: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reader.VerifyAsync(ProfileId, AttractionId, SessionId, PhotoKey, token));

        Assert.Equal(token, exception.CancellationToken);
    }
}
