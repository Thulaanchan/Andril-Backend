using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Andril.Common.DTOs;
using Andril.Common.Entities;
using Andril.Common.Interfaces;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<PhotoState>))]
public enum PhotoState { PendingStorage, Stored, Failed }
[JsonConverter(typeof(JsonStringEnumConverter<PhotoReason>))]
public enum PhotoReason { None, StorageFailure, UploadAbandoned, IntegrityFailure }

public sealed record PrivatePhotoStatus(Guid UploadId, PhotoState State, PhotoReason Reason, bool IsCurrent);
public sealed record PhotoScope(Guid ProfileId, string Module);
public sealed record SafeProfileImage(byte[] Bytes, string MediaType, int Width, int Height);
public sealed record PhotoUploadEvidence(string OriginalHash, string StoredHash, string MediaType, int Length, int Width, int Height);
public sealed record PhotoObjectAddress(PhotoScope Scope, Guid UploadId)
{
    public bool IsValid => Scope.ProfileId != Guid.Empty && ModuleNames.IsValid(Scope.Module) && UploadId != Guid.Empty;
    public string Key => IsValid ? $"{Scope.Module}/{Scope.ProfileId:D}/{UploadId:D}.jpg" : throw new PhotoUploadInvalidException();
}

public static class ProfilePhotoLimits
{
    public const int UploadBytes = 5_242_880;
    public const int InputMinimum = 300;
    public const int InputMaximum = 4096;
    public const int InputPixels = InputMaximum * InputMaximum;
    public const int OutputDimension = 1024;
    public const int JpegQuality = 85;
    public const int ConcurrentDecodes = 2;
}

public static class PhotoHashes
{
    public static string Compute(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static bool IsValid(string? hash) =>
        hash is { Length: 64 } && hash.All(static ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool Matches(StoredProfilePhoto expected, byte[]? bytes) =>
        bytes is { Length: > 0 } && bytes.Length == expected.Length
        && bytes.Length <= ProfilePhotoLimits.UploadBytes && Compute(bytes) == expected.Sha256;
}

public sealed record PhotoCleanupSettings(int BudgetSeconds, int RetryDelaySeconds, int MaximumCandidates)
{
    public const int ApprovedBudgetSeconds = 5;
    public const int ApprovedRetryDelaySeconds = 60;
    public const int ApprovedMaximumCandidates = 4;
    public static PhotoCleanupSettings Approved { get; } =
        new(ApprovedBudgetSeconds, ApprovedRetryDelaySeconds, ApprovedMaximumCandidates);

    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);
    public TimeSpan RetryDelay => TimeSpan.FromSeconds(RetryDelaySeconds);
}

public sealed record PhotoCleanupClaim(PhotoObjectAddress Address, Guid Token);

public interface IPhotoUploadPolicy
{
    int RequireMaximumBytes();
    PhotoCleanupSettings RequireCleanupSettings() => PhotoCleanupSettings.Approved;
}

public interface IProfileImageInspector
{
    bool IsApprovedAndConfigured { get; }
    // A future implementation must fully decode, validate and sanitize before returning JPEG.
    Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken cancellationToken);
}

public interface IPrivateProfilePhotoStore
{
    Task<PrivateProfilePhoto?> FindRequestAsync(PhotoScope scope, Guid requestId, CancellationToken ct);
    Task<PrivateProfilePhoto> ReserveAsync(PhotoScope scope, Guid requestId, string originalHash, PhotoUploadEvidence? evidence, CancellationToken ct);
    Task<bool> OwnsLeaseAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct);
    // Finalize inside the transaction opened by IPrivateProfileService.WithAuthorizedProfileAsync.
    Task<PrivatePhotoStatus> FinalizeAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct);
    Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct);
    Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct);
    Task<StoredProfilePhoto?> ReadCurrentAsync(PhotoScope scope, CancellationToken ct);
    Task<PhotoCleanupClaim?> ClaimCleanupAsync(PhotoScope scope, CancellationToken ct);
    Task<bool> RecordCleanupAsync(PhotoCleanupClaim claim, bool removed, TimeSpan retryDelay, CancellationToken ct);
}

public interface IProfilePhotoService
{
    Task<PrivatePhotoStatus> UploadAsync(string module, Guid requestId, Stream input, CancellationToken cancellationToken = default);
    Task<PrivatePhotoStatus> GetAsync(string module, Guid uploadId, CancellationToken cancellationToken = default);
    Task<PrivatePhotoStatus> GetCurrentAsync(string module, CancellationToken cancellationToken = default);
    Task<byte[]> GetContentAsync(string module, Guid uploadId, CancellationToken cancellationToken = default);
}

public sealed class PhotoDependencyException : Exception { }
public sealed class PhotoUploadInvalidException : Exception { }
public sealed class PhotoUploadTooLargeException : Exception { }
public sealed class PhotoUploadConflictException : Exception { }

public sealed class ProfilePhotoService(IPrivateProfileService profiles, IPhotoUploadPolicy policy,
    IProfileImageInspector inspector, IPrivateProfilePhotoStore store, IPrivateProfilePhotoObjectStorage objects,
    TimeProvider? timeProvider = null) : IProfilePhotoService
{
    private async Task<PhotoScope> ScopeAsync(string module, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(module, ct);
        return new(profile.ProfileId, profile.Module);
    }

    private int RequireReady()
    {
        if (!inspector.IsApprovedAndConfigured || !objects.IsConfigured) throw new PhotoDependencyException();
        int limit = policy.RequireMaximumBytes();
        return limit is > 0 and <= ProfilePhotoLimits.UploadBytes ? limit : throw new PhotoDependencyException();
    }

    public async Task<PrivatePhotoStatus> GetAsync(string module, Guid uploadId, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(module, cancellationToken);
        RequireId(uploadId);
        RequireReady();
        return await store.ReadAsync(scope, uploadId, cancellationToken) ?? throw new ProfileMissingException();
    }

    public async Task<PrivatePhotoStatus> GetCurrentAsync(string module, CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(module, cancellationToken);
        RequireReady();
        var current = await CurrentAsync(scope, cancellationToken);
        return new(current.Address.UploadId, PhotoState.Stored, PhotoReason.None, true);
    }

    public async Task<byte[]> GetContentAsync(string module, Guid uploadId, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var scope = await ScopeAsync(module, ct);
        RequireId(uploadId);
        RequireReady();
        var current = await CurrentAsync(scope, ct);
        if (current.Address.UploadId != uploadId) throw new ProfileMissingException();
        var result = await objects.ReadAsync(current, ct);
        if (result.Outcome != PhotoObjectOutcome.Success || !PhotoHashes.Matches(current, result.Bytes))
            throw new PhotoDependencyException();

        await RecheckAsync(scope, ct);
        if (current != await CurrentAsync(scope, ct)) throw new ProfileMissingException();
        return result.Bytes!;
    }

    public async Task<PrivatePhotoStatus> UploadAsync(string module, Guid requestId, Stream input, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var scope = await ScopeAsync(module, ct);
        RequireId(requestId);
        int limit = RequireReady();
        var original = await ReadInputAsync(input, limit, ct);
        var originalHash = PhotoHashes.Compute(original);
        var existing = await store.FindRequestAsync(scope, requestId, ct);
        if (existing is not null)
        {
            if (existing.OriginalContentHash != originalHash) throw new PhotoUploadConflictException();
            if (existing.State != PhotoState.PendingStorage)
            {
                var replay = await store.ReadAsync(scope, existing.Id, ct) ?? throw new ProfileMissingException();
                await TryCleanupAsync(scope, ct);
                return replay;
            }
        }

        SafeProfileImage? image = existing is null ? await InspectAsync(original, limit, ct) : null;
        PhotoUploadEvidence? evidence = image is null ? null :
            new(originalHash, PhotoHashes.Compute(image.Bytes), image.MediaType, image.Bytes.Length, image.Width, image.Height);
        await RecheckAsync(scope, ct);
        var pending = await store.ReserveAsync(scope, requestId, originalHash, evidence, ct);
        if (pending.State != PhotoState.PendingStorage) return pending.Status();

        var expected = pending.Evidence();
        var token = pending.LeaseToken ?? throw new PhotoDependencyException();
        var observed = await objects.ReadAsync(expected, ct);
        if (observed.Outcome == PhotoObjectOutcome.Missing)
        {
            image ??= await InspectAsync(original, limit, ct);
            if (!PhotoHashes.Matches(expected, image.Bytes) || image.Width != expected.Width || image.Height != expected.Height)
                return await FailAsync(scope, pending.Id, token, PhotoReason.IntegrityFailure, ct);

            await RecheckAsync(scope, ct);
            if (!await store.OwnsLeaseAsync(scope, pending.Id, token, ct))
                throw new PhotoUploadConflictException();

            var uploaded = await objects.UploadAsync(expected, image.Bytes, ct);
            if (uploaded == PhotoObjectOutcome.PermanentFailure)
                return await FailAsync(scope, pending.Id, token, PhotoReason.StorageFailure, ct);
            if (uploaded is not (PhotoObjectOutcome.Success or PhotoObjectOutcome.Conflict))
                throw new PhotoDependencyException();
            observed = await objects.ReadAsync(expected, ct);
        }

        if (observed.Outcome == PhotoObjectOutcome.IntegrityFailure ||
            (observed.Outcome == PhotoObjectOutcome.Success && !PhotoHashes.Matches(expected, observed.Bytes)))
            return await FailAsync(scope, pending.Id, token, PhotoReason.IntegrityFailure, ct);

        if (observed.Outcome != PhotoObjectOutcome.Success) throw new PhotoDependencyException();

        var status = await profiles.WithAuthorizedProfileAsync(module, async profile =>
        {
            if (profile.ProfileId != scope.ProfileId || profile.Module != scope.Module) throw new ProfileAccessDeniedException();
            return await store.FinalizeAsync(scope, pending.Id, token, ct);
        }, ct);
        await TryCleanupAsync(scope, ct);
        return status;
    }

    private async Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct)
    {
        await RecheckAsync(scope, ct);
        var failed = await store.FailAsync(scope, id, token, reason, ct);
        await TryCleanupAsync(scope, ct);
        return failed;
    }

    private async Task TryCleanupAsync(PhotoScope scope, CancellationToken ct)
    {
        try
        {
            var settings = policy.RequireCleanupSettings();
            if (settings != PhotoCleanupSettings.Approved) throw new PhotoDependencyException();
            var clock = timeProvider ?? TimeProvider.System;
            long started = clock.GetTimestamp();
            using var deadline = new CancellationTokenSource(settings.Budget, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var cleanupToken = linked.Token;

            cleanupToken.ThrowIfCancellationRequested();
            await RecheckAsync(scope, cleanupToken);
            for (int attempt = 0; attempt < settings.MaximumCandidates; attempt++)
            {
                if (cleanupToken.IsCancellationRequested || clock.GetElapsedTime(started) >= settings.Budget) return;
                var claim = await store.ClaimCleanupAsync(scope, cleanupToken);
                if (claim is null) return;
                if (claim.Address.Scope != scope || !claim.Address.IsValid || claim.Token == Guid.Empty)
                    throw new PhotoDependencyException();

                // The claim survives cancellation. An unrecorded attempt becomes eligible on expiry.
                var remaining = settings.Budget - clock.GetElapsedTime(started);
                if (cleanupToken.IsCancellationRequested || remaining <= TimeSpan.Zero) return;
                using var attemptDeadline = new CancellationTokenSource(remaining, clock);
                using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(cleanupToken, attemptDeadline.Token);
                var outcome = await objects.DeleteAsync(claim.Address, attemptToken.Token);
                if (cleanupToken.IsCancellationRequested || clock.GetElapsedTime(started) >= settings.Budget) return;
                await store.RecordCleanupAsync(claim, outcome is PhotoObjectOutcome.Success or PhotoObjectOutcome.Missing,
                    settings.RetryDelay, cleanupToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The upload result already committed. Keep unfinished claims durable without leaking provider details.
            System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("profile_photo_cleanup_deferred"));
        }
    }

    private async Task<StoredProfilePhoto> CurrentAsync(PhotoScope scope, CancellationToken ct)
    {
        var photo = await store.ReadCurrentAsync(scope, ct);
        if (photo is not { State: PhotoState.Stored, IsCurrent: true } || photo.Address.Scope != scope || !photo.Address.IsValid)
            throw new ProfileMissingException();
        return photo;
    }

    private async Task RecheckAsync(PhotoScope scope, CancellationToken ct)
    {
        if (scope != await ScopeAsync(scope.Module, ct)) throw new ProfileAccessDeniedException();
    }

    private async Task<SafeProfileImage> InspectAsync(byte[] original, int limit, CancellationToken ct)
    {
        var image = await inspector.InspectAsync(original, ct);
        if (image.MediaType != "image/jpeg" || image.Bytes.Length < 1 || image.Bytes.Length > limit
            || image.Width is < 1 or > ProfilePhotoLimits.OutputDimension
            || image.Height is < 1 or > ProfilePhotoLimits.OutputDimension)
            throw new PhotoUploadInvalidException();
        return image;
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty) throw new PhotoUploadInvalidException();
    }

    private static async Task<byte[]> ReadInputAsync(Stream input, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length + 1)), ct);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new PhotoUploadTooLargeException();
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length == 0) throw new PhotoUploadInvalidException();
        return buffer.ToArray();
    }
}

public sealed class UnavailableProfilePhotos : IProfileImageInspector
{
    public bool IsApprovedAndConfigured => false;
    public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken cancellationToken) => throw new PhotoDependencyException();
}
