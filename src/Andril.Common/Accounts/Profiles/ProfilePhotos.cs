using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<PhotoState>))]
public enum PhotoState { PendingValidation, PendingReview, Rejected, Approved }
[JsonConverter(typeof(JsonStringEnumConverter<PhotoReason>))]
public enum PhotoReason { None, InvalidImage, NoFace, MultipleFaces, Blurred, Pixelated, Censored, Obstructed, HeavyFilter, ReviewRequired }
public sealed record PhotoDecision(PhotoState State, PhotoReason Reason);
public sealed record PrivatePhotoStatus(Guid UploadId, PhotoState State, PhotoReason Reason);
public sealed record PhotoScope(Guid ProfileId, string Module);

/// <summary>Approved limits are deployment dependencies, not invented face quality thresholds.</summary>
public interface IPhotoUploadPolicy { int RequireMaximumBytes(); }
public sealed record SafeProfileImage(byte[] Bytes, string MediaType);
public interface IProfileImageInspector
{
    bool IsApprovedAndConfigured { get; }
    // Fully decode, bound decompression/dimensions, reject unsafe content, strip metadata and
    // re-encode an approved format. Never trust filenames, MIME headers or magic bytes alone.
    Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken cancellationToken);
}
public interface IProfileFaceValidator
{
    bool IsApprovedAndConfigured { get; }
    // Exactly one sufficiently visible human face; uncertain -> PendingReview.
    // Never identify people, compare modules, rank attractiveness or discriminate by
    // skin tone, disability, facial differences, headwear or ordinary accessories.
    // No external processing without explicit approval; no invented confidence thresholds.
    Task<PhotoDecision> ValidateAsync(SafeProfileImage image, CancellationToken cancellationToken);
}
public interface IPrivateProfilePhotoStore
{
    bool IsApprovedAndConfigured { get; }
    // Durable, atomic private quarantine + PendingValidation metadata. Scope is server-derived.
    // Same scope/key/content must reuse the upload; changed content/key collision -> conflict.
    // Never generate public URLs. Encrypt/restrict storage and enforce scope on every read.
    Task<PrivatePhotoStatus> BeginAsync(PhotoScope scope, Guid requestId, SafeProfileImage image, CancellationToken cancellationToken);
    // Monotonic compare-and-set: only PendingValidation may transition. Queue human review
    // atomically for PendingReview; review access/retention must be approved before deployment.
    Task<PrivatePhotoStatus> FinishAsync(PhotoScope scope, Guid uploadId, PhotoDecision decision, CancellationToken cancellationToken);
    Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid uploadId, CancellationToken cancellationToken);
}
public interface IProfilePhotoService
{
    Task<PrivatePhotoStatus> UploadAsync(string module, Guid requestId, Stream input, CancellationToken cancellationToken = default);
    Task<PrivatePhotoStatus> GetAsync(string module, Guid uploadId, CancellationToken cancellationToken = default);
}
public sealed class PhotoDependencyException : Exception { }
public sealed class PhotoUploadInvalidException : Exception { }
public sealed class PhotoUploadConflictException : Exception { }

public sealed class ProfilePhotoService(IPrivateProfileService profiles, IPhotoUploadPolicy policy,
    IProfileImageInspector inspector, IProfileFaceValidator validator, IPrivateProfilePhotoStore store) : IProfilePhotoService
{
    private async Task<PhotoScope> Scope(string module, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(module, ct); // fresh identity + eligibility/policies/membership
        return new(profile.ProfileId, profile.Module);
    }
    public async Task<PrivatePhotoStatus> GetAsync(string module, Guid uploadId, CancellationToken cancellationToken = default)
    {
        var scope = await Scope(module, cancellationToken);
        if (uploadId == Guid.Empty) throw new PhotoUploadInvalidException();
        if (!store.IsApprovedAndConfigured) throw new PhotoDependencyException();
        return await store.ReadAsync(scope, uploadId, cancellationToken) ?? throw new ProfileMissingException();
    }
    public async Task<PrivatePhotoStatus> UploadAsync(string module, Guid requestId, Stream input, CancellationToken cancellationToken = default)
    {
        var scope = await Scope(module, cancellationToken);
        if (requestId == Guid.Empty) throw new PhotoUploadInvalidException();
        if (!inspector.IsApprovedAndConfigured || !validator.IsApprovedAndConfigured || !store.IsApprovedAndConfigured)
            throw new PhotoDependencyException();
        var max = policy.RequireMaximumBytes();
        if (max <= 0) throw new PhotoDependencyException();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > max) throw new PhotoUploadInvalidException();
            buffer.Write(chunk, 0, count);
        }
        if (buffer.Length == 0) throw new PhotoUploadInvalidException();
        var image = await inspector.InspectAsync(buffer.ToArray(), cancellationToken);
        if (image.Bytes.Length == 0 || image.Bytes.Length > max || image.MediaType is not ("image/jpeg" or "image/png"))
            throw new PhotoUploadInvalidException();
        // Recheck after potentially slow decoding; do not save against stale authorization.
        if (scope != await Scope(module, cancellationToken)) throw new ProfileAccessDeniedException();
        var pending = await store.BeginAsync(scope, requestId, image, cancellationToken);
        if (pending.State != PhotoState.PendingValidation) return pending;
        var decision = await validator.ValidateAsync(image, cancellationToken);
        if (!Enum.IsDefined(decision.State) || !Enum.IsDefined(decision.Reason)
            || decision.State == PhotoState.PendingValidation
            || decision.State == PhotoState.Approved && decision.Reason != PhotoReason.None
            || decision.State == PhotoState.Rejected && decision.Reason is PhotoReason.None or PhotoReason.ReviewRequired)
            throw new PhotoDependencyException();
        if (decision.State == PhotoState.PendingReview) decision = new(PhotoState.PendingReview, PhotoReason.ReviewRequired);
        if (scope != await Scope(module, cancellationToken)) throw new ProfileAccessDeniedException();
        return await store.FinishAsync(scope, pending.UploadId, decision, cancellationToken);
    }
}

// Production registration remains disabled. No upload bytes are read or stored, and
// no automated approval is possible until all reviewed infrastructure is supplied.
public sealed class UnavailableProfilePhotos : IPhotoUploadPolicy, IProfileImageInspector, IProfileFaceValidator, IPrivateProfilePhotoStore
{
    public bool IsApprovedAndConfigured => false;
    public int RequireMaximumBytes() => throw new PhotoDependencyException();
    public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken ct) => throw new PhotoDependencyException();
    public Task<PhotoDecision> ValidateAsync(SafeProfileImage image, CancellationToken ct) => throw new PhotoDependencyException();
    public Task<PrivatePhotoStatus> BeginAsync(PhotoScope scope, Guid requestId, SafeProfileImage image, CancellationToken ct) => throw new PhotoDependencyException();
    public Task<PrivatePhotoStatus> FinishAsync(PhotoScope scope, Guid uploadId, PhotoDecision decision, CancellationToken ct) => throw new PhotoDependencyException();
    public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid uploadId, CancellationToken ct) => throw new PhotoDependencyException();
}
