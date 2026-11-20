using Andril.Common.Accounts.Profiles;
using Andril.Common.Entities;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class PrivateProfilePhotoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly PhotoUploadEvidence Evidence = new(new string('a', 64), new string('b', 64), "image/jpeg", 150, 300, 300);

    [Fact]
    public void PublicationAndSupersedingAreOneWay()
    {
        var photo = Create();
        var token = photo.LeaseToken!.Value;
        Assert.False(photo.IsCurrent);
        Assert.Equal($"social/{photo.ProfileId:D}/{photo.Id:D}.jpg", photo.ObjectKey);
        photo.MarkStored(token, Now);
        Assert.True(photo.IsCurrent);
        Assert.Null(photo.LeaseToken);
        photo.MarkSuperseded(Now.AddSeconds(1));
        Assert.Equal(PhotoState.Stored, photo.State);
        Assert.False(photo.IsCurrent);
        Assert.True(photo.CleanupPending);
        Assert.Throws<PhotoUploadConflictException>(() => photo.MarkStored(token, Now.AddSeconds(2)));
        Record(photo, false, Now.AddSeconds(2));
        Assert.True(photo.CleanupPending);
        Assert.False(photo.CanClaimCleanup(Now.AddSeconds(61)));
        Record(photo, true, Now.AddSeconds(62));
        Assert.False(photo.CleanupPending);
    }

    [Fact]
    public void ExpiryFencesOldOwnerAndRetainsLateWriteEvidenceAfterMissingCleanup()
    {
        var photo = Create();
        var old = photo.LeaseToken!.Value;
        var replacement = Guid.NewGuid();
        Assert.Throws<PhotoUploadConflictException>(() => photo.Abandon(Now));
        photo.Reclaim(replacement, Now.AddMinutes(2), Now.AddMinutes(4));
        Assert.Throws<PhotoUploadConflictException>(() => photo.MarkStored(old, Now.AddMinutes(2)));
        photo.Abandon(Now.AddMinutes(4));
        Record(photo, true, Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(6), photo.NextCleanupAttemptAtUtc);
        Assert.True(photo.LateWriteRisk);
        Assert.True(photo.CleanupPending);
        Assert.Equal(PhotoReason.UploadAbandoned, photo.Reason);
        Assert.Throws<PhotoUploadConflictException>(() => photo.MarkStored(replacement, Now.AddMinutes(5)));
    }

    [Fact]
    public void ConfirmedFailureIsTerminalAndEligibleForCleanup()
    {
        var photo = Create();
        var token = photo.LeaseToken!.Value;
        photo.MarkFailed(token, PhotoReason.StorageFailure, Now);
        Assert.Equal(PhotoState.Failed, photo.State);
        Assert.False(photo.IsCurrent);
        Assert.True(photo.CleanupPending);
        Assert.Throws<PhotoUploadConflictException>(() => photo.MarkStored(token, Now));
        Record(photo, true, Now);
        Assert.False(photo.CleanupPending);
    }

    [Fact]
    public void RejectsInvalidMetadataAndIdentifiers()
    {
        var scope = new PhotoScope(Guid.NewGuid(), "social");
        var id = Guid.NewGuid();
        var request = Guid.NewGuid();
        var token = Guid.NewGuid();
        Assert.Throws<PhotoUploadInvalidException>(() => PrivateProfilePhoto.Create(scope, Guid.Empty, request, Evidence, token, Now, Now.AddMinutes(1)));
        Assert.Throws<PhotoUploadInvalidException>(() => PrivateProfilePhoto.Create(scope, id, id, Evidence, token, Now, Now.AddMinutes(1)));
        Assert.Throws<PhotoUploadInvalidException>(() => PrivateProfilePhoto.Create(scope with { Module = "dating" }, id, request, Evidence, token, Now, Now.AddMinutes(1)));
        foreach (var invalid in new[] { Evidence with { Length = 0 }, Evidence with { Length = 5242881 },
            Evidence with { Width = 1025 }, Evidence with { Height = 0 }, Evidence with { MediaType = "image/png" },
            Evidence with { OriginalHash = new string('G', 64) }, Evidence with { StoredHash = "bad" } })
            Assert.Throws<PhotoUploadInvalidException>(() => PrivateProfilePhoto.Create(scope, id, request, invalid, token, Now, Now.AddMinutes(1)));
    }

    [Fact]
    public void ActiveClaimIsExclusiveAndExpiredOwnerCannotClearReclaimedEvidence()
    {
        var photo = Create();
        Assert.False(photo.CanClaimCleanup(Now));
        photo.MarkStored(photo.LeaseToken!.Value, Now);
        Assert.False(photo.CanClaimCleanup(Now));
        photo.MarkSuperseded(Now);
        var old = photo.ClaimCleanup(Guid.NewGuid(), Now, Now.AddSeconds(65));
        Assert.False(photo.CanClaimCleanup(Now.AddSeconds(64)));
        Assert.Throws<PhotoUploadConflictException>(() =>
            photo.ClaimCleanup(Guid.NewGuid(), Now.AddSeconds(64), Now.AddSeconds(129)));

        var current = photo.ClaimCleanup(Guid.NewGuid(), Now.AddSeconds(65), Now.AddSeconds(130));
        Assert.False(photo.RecordCleanupAttempt(old.Token, true, PhotoCleanupSettings.Approved.RetryDelay, Now.AddSeconds(66)));
        Assert.Equal(current.Token, photo.CleanupClaimId);
        Assert.True(photo.RecordCleanupAttempt(current.Token, false, PhotoCleanupSettings.Approved.RetryDelay, Now.AddSeconds(66)));
        Assert.Equal(Now.AddSeconds(126), photo.NextCleanupAttemptAtUtc);
        Assert.True(photo.CleanupPending);
        Assert.False(photo.IsCurrent);
    }

    private static void Record(PrivateProfilePhoto photo, bool removed, DateTimeOffset now)
    {
        var claim = photo.ClaimCleanup(Guid.NewGuid(), now, now.AddSeconds(65));
        Assert.True(photo.RecordCleanupAttempt(claim.Token, removed, PhotoCleanupSettings.Approved.RetryDelay, now));
    }

    private static PrivateProfilePhoto Create() => PrivateProfilePhoto.Create(new(Guid.NewGuid(), "social"), Guid.NewGuid(),
        Guid.NewGuid(), Evidence, Guid.NewGuid(), Now, Now.AddMinutes(2));
}
