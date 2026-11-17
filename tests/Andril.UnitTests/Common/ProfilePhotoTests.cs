using Andril.Common.Accounts.Profiles;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ProfilePhotoTests
{
    [Theory]
    [InlineData("  ALIce  ", "alice")]
    [InlineData("\u00a0ABC\u2000", "abc")]
    [InlineData("ÄA", "Äa")]
    public void NamesUseTrimAndAsciiOnly(string value, string expected) => Assert.Equal(expected, DisplayNames.Canonical(value));

    [Theory]
    [InlineData(PhotoReason.Blurred)]
    [InlineData(PhotoReason.Pixelated)]
    [InlineData(PhotoReason.Censored)]
    [InlineData(PhotoReason.HeavyFilter)]
    [InlineData(PhotoReason.MultipleFaces)]
    [InlineData(PhotoReason.NoFace)]
    [InlineData(PhotoReason.Obstructed)]
    public async Task TrustedRejectionsRemainPrivateAndPermitNewUpload(PhotoReason reason)
    {
        var f = new Fixture { Decision = new(PhotoState.Rejected, reason) };
        var result = await f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1, 2]));
        Assert.Equal(PhotoState.Rejected, result.State);
        Assert.Equal(reason, result.Reason);
        f.Decision = new(PhotoState.Approved, PhotoReason.None);
        var next = await f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([3]));
        Assert.Equal(PhotoState.Approved, next.State);
        Assert.NotEqual(result.UploadId, next.UploadId);
    }

    [Fact]
    public async Task ApprovedAndUncertainResultsUseTrustedStatesWithIdempotency()
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        var result = await f.Service.UploadAsync("learning", id, new MemoryStream([1]));
        Assert.Equal(PhotoState.Approved, result.State);
        Assert.Equal(result, await f.Service.UploadAsync("learning", id, new MemoryStream([1])));
        await Assert.ThrowsAsync<PhotoUploadConflictException>(() => f.Service.UploadAsync("learning", id, new MemoryStream([2])));
        f.Decision = new(PhotoState.PendingReview, PhotoReason.None);
        result = await f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([2]));
        Assert.Equal(PhotoState.PendingReview, result.State);
        Assert.Equal(PhotoReason.ReviewRequired, result.Reason);
    }

    [Fact]
    public async Task InvalidUploadsDependenciesAndOwnershipFailClosed()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([])));
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream(new byte[17])));
        f.InvalidImage = true;
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1])));
        Assert.Empty(f.Rows);
        f.InvalidImage = false; f.Approved = false;
        await Assert.ThrowsAsync<PhotoDependencyException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1])));
        f.Approved = true; f.Denied = true;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1])));
        Assert.Empty(f.Rows);
    }

    [Fact]
    public async Task StatusIsScopedAndInvalidProviderApprovalIsDenied()
    {
        var f = new Fixture();
        var result = await f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1]));
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetAsync("hobbies", result.UploadId));
        f.ProfileId = Guid.NewGuid();
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetAsync("learning", result.UploadId));
        f.Decision = new(PhotoState.Approved, PhotoReason.NoFace);
        await Assert.ThrowsAsync<PhotoDependencyException>(() => f.Service.UploadAsync("learning", Guid.NewGuid(), new MemoryStream([1])));
        Assert.Contains(f.Rows.Values, x => x.Status.State == PhotoState.PendingValidation);
    }

    private sealed class Fixture : IPrivateProfileService, IPhotoUploadPolicy, IProfileImageInspector, IProfileFaceValidator, IPrivateProfilePhotoStore
    {
        internal Guid ProfileId = Guid.NewGuid();
        internal bool Approved = true, Denied, InvalidImage;
        internal PhotoDecision Decision = new(PhotoState.Approved, PhotoReason.None);
        internal Dictionary<(PhotoScope, Guid), (byte[] Bytes, PrivatePhotoStatus Status)> Rows = [];
        internal ProfilePhotoService Service => new(this, this, this, this, this);
        public bool IsApprovedAndConfigured => Approved;
        public int RequireMaximumBytes() => 16; // test-only transport limit, not a quality threshold
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken ct = default) => Denied
            ? throw new ProfileAccessDeniedException() : Task.FromResult(new PrivateProfileDto(ProfileId, module, DateTimeOffset.UtcNow));
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken ct) => InvalidImage
            ? throw new PhotoUploadInvalidException() : Task.FromResult(new SafeProfileImage(bytes, "image/png"));
        public Task<PhotoDecision> ValidateAsync(SafeProfileImage image, CancellationToken ct) => Task.FromResult(Decision);
        public Task<PrivatePhotoStatus> BeginAsync(PhotoScope scope, Guid requestId, SafeProfileImage image, CancellationToken ct)
        {
            if (Rows.TryGetValue((scope, requestId), out var old))
            {
                if (!old.Bytes.SequenceEqual(image.Bytes)) throw new PhotoUploadConflictException();
                return Task.FromResult(old.Status);
            }
            var status = new PrivatePhotoStatus(requestId, PhotoState.PendingValidation, PhotoReason.None);
            Rows.Add((scope, requestId), (image.Bytes, status));
            return Task.FromResult(status);
        }
        public Task<PrivatePhotoStatus> FinishAsync(PhotoScope scope, Guid id, PhotoDecision decision, CancellationToken ct)
        {
            var row = Rows[(scope, id)];
            if (row.Status.State == PhotoState.PendingValidation)
                Rows[(scope, id)] = row with { Status = new(id, decision.State, decision.Reason) };
            return Task.FromResult(Rows[(scope, id)].Status);
        }
        public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct) =>
            Task.FromResult(Rows.TryGetValue((scope, id), out var row) ? row.Status : null);
    }
}
