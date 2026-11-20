using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryPrivateMediaAccessReaderTests
{
    private static readonly Guid MediaId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherMediaId = new("22222222-2222-4222-8222-222222222222");
    private readonly IGalleryPrivateMediaAccessReader _reader = new DenyAllGalleryPrivateMediaAccessReader();

    [Fact]
    public void Contract_AcceptsMediaIdAndCancellationWithoutCallerSuppliedScopeOrObjectKey()
    {
        var method = Assert.Single(typeof(IGalleryPrivateMediaAccessReader).GetMethods());
        Assert.Equal(new[] { "mediaId", "cancellationToken" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(new[] { typeof(Guid), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task FindAuthorizedAsync_DeniesEmptyMediaId()
    {
        Assert.Null(await _reader.FindAuthorizedAsync(Guid.Empty));
    }

    [Fact]
    public async Task FindAuthorizedAsync_DeniesWellFormedMediaIdWithoutAuthorization()
    {
        Assert.Null(await _reader.FindAuthorizedAsync(MediaId));
    }

    [Fact]
    public async Task FindAuthorizedAsync_DeniesDifferentMediaIdsAndRepeatedRequestsUniformly()
    {
        foreach (var mediaId in new[] { MediaId, OtherMediaId, MediaId })
            Assert.Null(await _reader.FindAuthorizedAsync(mediaId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindAuthorizedAsync_ObservesPreCancellationBeforeDenial(bool emptyMediaId)
    {
        var token = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _reader.FindAuthorizedAsync(emptyMediaId ? Guid.Empty : MediaId, token));

        Assert.Equal(token, error.CancellationToken);
    }
}
