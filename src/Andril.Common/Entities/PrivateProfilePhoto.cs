using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;

namespace Andril.Common.Entities;

public sealed class PrivateProfilePhoto
{
    private PrivateProfilePhoto() { }

    public Guid Id { get; private set; }
    public Guid ProfileId { get; private set; }
    public string Module { get; private set; } = null!;
    public Guid RequestId { get; private set; }
    public string ObjectKey { get; private set; } = null!;
    public string OriginalContentHash { get; private set; } = null!;
    public string StoredContentHash { get; private set; } = null!;
    public string MediaType { get; private set; } = "image/jpeg";
    public int FileSizeBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public PhotoState State { get; private set; }
    public PhotoReason Reason { get; private set; }
    public bool IsCurrent { get; private set; }
    public bool CleanupPending { get; private set; }
    public bool LateWriteRisk { get; private set; }
    public DateTimeOffset? NextCleanupAttemptAtUtc { get; private set; }
    public Guid? CleanupClaimId { get; private set; }
    public DateTimeOffset? CleanupClaimExpiresAtUtc { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntilUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public static PrivateProfilePhoto Create(PhotoScope scope, Guid id, Guid requestId, PhotoUploadEvidence evidence,
        Guid leaseToken, DateTimeOffset now, DateTimeOffset leaseUntil)
    {
        var address = new PhotoObjectAddress(scope, id);
        if (!address.IsValid || requestId == Guid.Empty || id == requestId || leaseToken == Guid.Empty
            || !PhotoHashes.IsValid(evidence.OriginalHash) || !PhotoHashes.IsValid(evidence.StoredHash)
            || evidence.MediaType != "image/jpeg" || evidence.Length is < 1 or > ProfilePhotoLimits.UploadBytes
            || evidence.Width is < 1 or > ProfilePhotoLimits.OutputDimension
            || evidence.Height is < 1 or > ProfilePhotoLimits.OutputDimension
            || !IsUtc(now) || !IsUtc(leaseUntil) || leaseUntil <= now)
            throw new PhotoUploadInvalidException();

        return new()
        {
            Id = id, ProfileId = scope.ProfileId, Module = scope.Module, RequestId = requestId,
            ObjectKey = address.Key, OriginalContentHash = evidence.OriginalHash, StoredContentHash = evidence.StoredHash,
            FileSizeBytes = evidence.Length, Width = evidence.Width, Height = evidence.Height,
            State = PhotoState.PendingStorage, LeaseToken = leaseToken, LeaseUntilUtc = leaseUntil,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
    }

    public bool OwnsLease(Guid token, DateTimeOffset now) =>
        State == PhotoState.PendingStorage && token != Guid.Empty && LeaseToken == token && LeaseUntilUtc > now;

    public void Reclaim(Guid token, DateTimeOffset now, DateTimeOffset until)
    {
        if (State != PhotoState.PendingStorage || LeaseUntilUtc > now || token == Guid.Empty
            || token == LeaseToken || !IsUtc(until) || until <= now) throw new PhotoUploadConflictException();
        Touch(now);
        LeaseToken = token;
        LeaseUntilUtc = until;
        LateWriteRisk = true;
    }

    public void MarkStored(Guid token, DateTimeOffset now)
    {
        RequireLease(token, now);
        Touch(now);
        State = PhotoState.Stored;
        IsCurrent = true;
        ClearLease();
    }

    public void MarkFailed(Guid token, PhotoReason reason, DateTimeOffset now)
    {
        RequireLease(token, now);
        if (reason is not (PhotoReason.StorageFailure or PhotoReason.IntegrityFailure))
            throw new PhotoUploadInvalidException();
        Fail(reason, now);
    }

    public void Abandon(DateTimeOffset now)
    {
        if (State != PhotoState.PendingStorage || LeaseUntilUtc > now) throw new PhotoUploadConflictException();
        Fail(PhotoReason.UploadAbandoned, now);
        LateWriteRisk = true;
    }

    public void MarkSuperseded(DateTimeOffset now)
    {
        if (State != PhotoState.Stored || !IsCurrent) throw new PhotoUploadConflictException();
        Touch(now);
        IsCurrent = false;
        SupersededAtUtc = now;
        QueueCleanup(now);
    }

    // Explicit removal hides the reference without authorizing irreversible purge.
    // Preserve the immutable key for later approved retention/provider reconciliation.
    // A reclaimed upload's unresolved external write risk needs that review first.
    public void DetachForRetentionReview(DateTimeOffset now)
    {
        if (State != PhotoState.Stored || !IsCurrent) throw new PhotoUploadConflictException();
        if (LateWriteRisk) throw new PhotoDependencyException();
        Touch(now);
        IsCurrent = false;
        SupersededAtUtc = now;
    }

    public bool CanClaimCleanup(DateTimeOffset now) => CleanupPending && !IsCurrent
        && (State == PhotoState.Failed || State == PhotoState.Stored && SupersededAtUtc.HasValue)
        && NextCleanupAttemptAtUtc <= now
        && (CleanupClaimExpiresAtUtc is null || CleanupClaimExpiresAtUtc <= now);

    public PhotoCleanupClaim ClaimCleanup(Guid token, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (!CanClaimCleanup(now) || token == Guid.Empty || token == CleanupClaimId || !IsUtc(expiresAt) || expiresAt <= now)
            throw new PhotoUploadConflictException();
        Touch(now);
        CleanupClaimId = token;
        CleanupClaimExpiresAtUtc = expiresAt;
        return new(new(new(ProfileId, Module), Id), token);
    }

    public bool RecordCleanupAttempt(Guid token, bool confirmedRemoved, TimeSpan retryDelay, DateTimeOffset now)
    {
        if (!CleanupPending || IsCurrent || State == PhotoState.PendingStorage || token == Guid.Empty
            || CleanupClaimId != token || CleanupClaimExpiresAtUtc is null || CleanupClaimExpiresAtUtc <= now)
            return false;
        if (retryDelay != PhotoCleanupSettings.Approved.RetryDelay) throw new PhotoDependencyException();

        Touch(now);
        CleanupClaimId = null;
        CleanupClaimExpiresAtUtc = null;
        CleanupPending = !confirmedRemoved || LateWriteRisk;
        NextCleanupAttemptAtUtc = CleanupPending ? now + retryDelay : null;
        return true;
    }

    public PrivatePhotoStatus Status() => new(Id, State, Reason, IsCurrent);

    public StoredProfilePhoto Evidence() => new(new(new(ProfileId, Module), Id), State, IsCurrent,
        StoredContentHash, MediaType, FileSizeBytes, Width, Height);

    private void Fail(PhotoReason reason, DateTimeOffset now)
    {
        Touch(now);
        State = PhotoState.Failed;
        Reason = reason;
        QueueCleanup(now);
        ClearLease();
    }

    private void QueueCleanup(DateTimeOffset now)
    {
        CleanupPending = true;
        NextCleanupAttemptAtUtc = now;
    }

    private void RequireLease(Guid token, DateTimeOffset now)
    {
        if (!OwnsLease(token, now)) throw new PhotoUploadConflictException();
    }

    private void ClearLease()
    {
        LeaseToken = null;
        LeaseUntilUtc = null;
    }

    private void Touch(DateTimeOffset now)
    {
        if (!IsUtc(now) || now < UpdatedAtUtc) throw new PhotoDependencyException();
        UpdatedAtUtc = now;
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero
        && value != DateTimeOffset.MinValue && value != DateTimeOffset.MaxValue;
}
