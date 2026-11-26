using System.Text.Json.Serialization;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Shared.Authentication;

namespace Andril.Hobbies.Map.Contributions;

internal readonly record struct MapMediaScope(Guid SubjectId, Guid ProfileId, Guid AttractionId, Guid SessionId);
internal sealed record MapMediaReservation(
    MapMediaScope Scope, Guid MediaId, Guid CaptureChallenge, DateTimeOffset ReservedAt, DateTimeOffset UploadExpiresAt);
internal sealed record VerifiedMapMedia(
    [property: JsonIgnore] Guid MediaId,
    [property: JsonIgnore] string PrivateReference,
    [property: JsonIgnore] string Version,
    [property: JsonIgnore] string Sha256)
{
    public override string ToString() => "Verified private Map media.";
}
internal sealed record FinalizedMapMedia(MapMediaReservation Reservation, VerifiedMapMedia Media, DateTimeOffset CapturedAt);

internal sealed record MapUploadGrant(
    [property: JsonIgnore] Guid MediaId,
    [property: JsonIgnore] string Capability,
    [property: JsonIgnore] Guid CaptureChallenge = default,
    [property: JsonIgnore] DateTimeOffset UploadExpiresAt = default)
{
    public override string ToString() => "Private Map upload grant.";
}

/// <summary>Store operations must enforce one reservation per session across callers and processes.</summary>
internal interface IMapPrivateMediaStore
{
    Task<bool> TryReserveAsync(MapMediaReservation reservation, CancellationToken cancellationToken);
    Task<MapMediaReservation?> FindReservationAsync(MapMediaScope scope, CancellationToken cancellationToken);
    Task<FinalizedMapMedia?> FindFinalizedAsync(MapMediaScope scope, CancellationToken cancellationToken);

    // Atomically compare the reservation and finalize once under current scope
    // authorization. Cleanup must exclude finalization, and finalized evidence
    // must never be eligible for orphan deletion.
    Task<bool> TryFinalizeAsync(FinalizedMapMedia evidence, CancellationToken cancellationToken);
}

internal interface IMapPrivateMediaStorage
{
    // Issue a short-lived create-only capability for the server-owned identity after
    // checking the approved Map upload policy. Never return service credentials.
    Task<MapUploadGrant?> AuthorizeCreateAsync(MapMediaReservation reservation, CancellationToken cancellationToken);

    // Return only a current, private, immutable version with server-computed integrity
    // after the approved Map image policy passes. Deny when policy or storage is unavailable.
    Task<VerifiedMapMedia?> InspectAsync(MapMediaReservation reservation, CancellationToken cancellationToken);
}

internal interface IMapPrivateMediaObjectWriter
{
    Task<bool> CreateAsync(MapMediaReservation reservation, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<bool> DeleteOrphanAsync(MapMediaReservation reservation, CancellationToken cancellationToken);
}

internal interface IMapCameraProvenance
{
    // Return capture time only for independently trusted in-app capture of this
    // exact scope and immutable version. An upload, hash or client claim is insufficient.
    Task<DateTimeOffset?> VerifyCaptureAsync(
        MapMediaReservation reservation, VerifiedMapMedia media, CancellationToken cancellationToken);
}

/// <summary>Internal upload and evidence coordination; composed only with approved adapters.</summary>
internal sealed class MapPrivateMediaService(
    ICurrentUser currentUser,
    IMapCaptureSessionAccessReader access,
    IMapContributionCaptureSessionRepository sessions,
    IMapPrivateMediaStore store,
    IMapPrivateMediaStorage storage,
    IMapCameraProvenance provenance,
    TimeProvider clock) : IMapCameraEvidenceSource
{
    internal async Task<MapUploadGrant?> AuthorizeAsync(
        Guid attractionId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(attractionId, sessionId, cancellationToken);
        if (scope is null)
            return null;

        var session = await FindOwnedSessionAsync(scope.Value, cancellationToken);
        if (session is null || session.CompletedAtUtc is not null)
            return null;
        // Persist at PostgreSQL timestamp precision so exact retries compare the same values.
        var now = clock.GetUtcNow().ToUniversalTime();
        now = now.AddTicks(-(now.Ticks % 10));
        var expiry = now.AddSeconds(120);
        var sessionDeadline = session.StartedAtUtc + MapContributionLocationPolicy.MaximumCaptureDuration;
        if (sessionDeadline < expiry)
            expiry = sessionDeadline;
        if (now < session.StartedAtUtc || expiry <= now)
            return null;
        var reservation = new MapMediaReservation(scope.Value, Guid.NewGuid(), Guid.NewGuid(), now, expiry);
        if (!await store.TryReserveAsync(reservation, cancellationToken))
            return null;

        var grant = await storage.AuthorizeCreateAsync(reservation, cancellationToken);
        return grant is not null && grant.MediaId == reservation.MediaId
            && !string.IsNullOrWhiteSpace(grant.Capability)
            && (await FindOwnedSessionAsync(scope.Value, cancellationToken)) is { CompletedAtUtc: null }
            && clock.GetUtcNow() < reservation.UploadExpiresAt
            ? grant with { CaptureChallenge = reservation.CaptureChallenge, UploadExpiresAt = reservation.UploadExpiresAt }
            : null;
    }

    internal async Task<bool> UploadAsync(Guid attractionId, Guid sessionId, Guid mediaId, string capability,
        ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(attractionId, sessionId, cancellationToken);
        if (scope is null || mediaId == Guid.Empty || storage is not IMapPrivateMediaObjectWriter writer
            || !string.Equals(capability, mediaId.ToString("N"), StringComparison.Ordinal))
            return false;

        var reservation = await store.FindReservationAsync(scope.Value, cancellationToken);
        return reservation is not null && reservation.Scope == scope.Value && reservation.MediaId == mediaId
            && await store.FindFinalizedAsync(scope.Value, cancellationToken) is null
            && await FindCurrentSessionAsync(scope.Value, cancellationToken) is not null
            && clock.GetUtcNow() < reservation.UploadExpiresAt
            && await writer.CreateAsync(reservation, jpeg, cancellationToken);
    }

    internal async Task<bool> CompleteAsync(
        Guid attractionId, Guid sessionId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(attractionId, sessionId, cancellationToken);
        if (scope is null || mediaId == Guid.Empty
            || await FindCurrentSessionAsync(scope.Value, cancellationToken) is null)
            return false;

        var reservation = await store.FindReservationAsync(scope.Value, cancellationToken);
        if (reservation is null || reservation.Scope != scope.Value || reservation.MediaId != mediaId)
            return false;

        var media = await InspectAsync(reservation, cancellationToken);
        if (media is null)
            return false;

        var capturedAt = await provenance.VerifyCaptureAsync(reservation, media, cancellationToken);
        var session = await FindCurrentSessionAsync(scope.Value, cancellationToken);
        if (session is null || capturedAt is not { } captured || !IsWithinCapture(session, captured)
            || captured < reservation.ReservedAt || captured >= reservation.UploadExpiresAt)
            return false;

        captured = captured.ToUniversalTime();
        captured = captured.AddTicks(-(captured.Ticks % 10));
        var finalized = new FinalizedMapMedia(reservation, media, captured);
        if (await store.TryFinalizeAsync(finalized, cancellationToken))
            return true;

        // An exact concurrent retry may observe the winner. Never replace a binding.
        return await store.FindFinalizedAsync(scope.Value, cancellationToken) == finalized
            && await FindCurrentSessionAsync(scope.Value, cancellationToken) is not null;
    }

    public async Task<MapCameraEvidence?> FindAsync(
        Guid accountSubjectId, Guid ownerHobbiesModuleProfileId, Guid attractionId,
        Guid captureSessionId, CancellationToken cancellationToken)
    {
        var scope = new MapMediaScope(accountSubjectId, ownerHobbiesModuleProfileId, attractionId, captureSessionId);
        if (await FindCurrentSessionAsync(scope, cancellationToken) is null)
            return null;

        var finalized = await store.FindFinalizedAsync(scope, cancellationToken);
        if (finalized is null || finalized.Reservation.Scope != scope
            || await InspectAsync(finalized.Reservation, cancellationToken) != finalized.Media)
            return null;

        var session = await FindCurrentSessionAsync(scope, cancellationToken);
        return session is not null && IsWithinCapture(session, finalized.CapturedAt)
            ? new MapCameraEvidence(scope.SubjectId, scope.ProfileId, scope.AttractionId, scope.SessionId,
                finalized.Media.PrivateReference, finalized.CapturedAt, MapPhotoOrigin.InAppCamera)
            : null;
    }

    private async Task<VerifiedMapMedia?> InspectAsync(
        MapMediaReservation reservation, CancellationToken cancellationToken)
    {
        var media = await storage.InspectAsync(reservation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return media is not null && reservation.MediaId != Guid.Empty && media.MediaId == reservation.MediaId
            && !string.IsNullOrWhiteSpace(media.PrivateReference) && !string.IsNullOrWhiteSpace(media.Version)
            && media.Sha256 is { Length: 64 } && media.Sha256.All(Uri.IsHexDigit) ? media : null;
    }

    private async Task<MapMediaScope?> ResolveScopeAsync(
        Guid attractionId, Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty
            || attractionId == Guid.Empty || sessionId == Guid.Empty)
            return null;

        var owner = await access.ResolveAuthorizedProfileIdAsync(attractionId, cancellationToken);
        if (owner is not Guid profile || profile == Guid.Empty)
            return null;

        var scope = new MapMediaScope(subject, profile, attractionId, sessionId);
        return scope;
    }

    private async Task<MapContributionCaptureSession?> FindCurrentSessionAsync(
        MapMediaScope scope, CancellationToken cancellationToken)
    {
        var session = await FindOwnedSessionAsync(scope, cancellationToken);
        return session?.CompletedAtUtc is { } completed
            && completed - session.StartedAtUtc <= MapContributionLocationPolicy.MaximumCaptureDuration
            && MapContributionSubmissionWindow.Allows(session, clock.GetUtcNow()) ? session : null;
    }

    private async Task<MapContributionCaptureSession?> FindOwnedSessionAsync(
        MapMediaScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.SubjectId == Guid.Empty || scope.ProfileId == Guid.Empty
            || scope.AttractionId == Guid.Empty || scope.SessionId == Guid.Empty
            || !currentUser.IsAuthenticated || currentUser.UserId != scope.SubjectId
            || await access.ResolveAuthorizedProfileIdAsync(scope.AttractionId, cancellationToken) != scope.ProfileId)
            return null;

        var session = await sessions.FindOwnedAsync(scope.SessionId, scope.ProfileId, scope.AttractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return currentUser.IsAuthenticated && currentUser.UserId == scope.SubjectId
            && session is not null && session.SessionId == scope.SessionId
            && session.OwnerHobbiesModuleProfileId == scope.ProfileId && session.AttractionId == scope.AttractionId
            ? session : null;
    }

    private static bool IsWithinCapture(MapContributionCaptureSession session, DateTimeOffset capturedAt) =>
        session.CompletedAtUtc is { } completed && capturedAt >= session.StartedAtUtc && capturedAt <= completed;
}
