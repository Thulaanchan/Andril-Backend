namespace Andril.Hobbies.Map.Contributions;

internal sealed record MapCaptureStatement(
    MapMediaScope Scope, Guid MediaId, Guid Challenge, string Version, string Sha256,
    DateTimeOffset CapturedAt, bool ApplicationIntegrityVerified,
    bool DeviceIntegrityVerified, bool FreshCameraCaptureVerified);

// A future approved provider validates the independent integrity and camera
// signals. This port accepts no caller-supplied proof or client timestamps.
internal interface IMapCaptureStatementVerifier
{
    Task<MapCaptureStatement?> FindVerifiedAsync(
        MapMediaScope scope, Guid mediaId, Guid challenge, CancellationToken cancellationToken);
}

internal sealed class VerifiedMapCameraProvenance(IMapCaptureStatementVerifier verifier) : IMapCameraProvenance
{
    public async Task<DateTimeOffset?> VerifyCaptureAsync(
        MapMediaReservation reservation, VerifiedMapMedia media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reservation.CaptureChallenge == Guid.Empty || reservation.MediaId == Guid.Empty
            || reservation.MediaId != media.MediaId)
            return null;
        var statement = await verifier.FindVerifiedAsync(
            reservation.Scope, reservation.MediaId, reservation.CaptureChallenge, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return statement is { ApplicationIntegrityVerified: true, DeviceIntegrityVerified: true, FreshCameraCaptureVerified: true }
            && statement.Scope == reservation.Scope && statement.MediaId == media.MediaId
            && statement.Challenge == reservation.CaptureChallenge
            && statement.CapturedAt >= reservation.ReservedAt
            && statement.CapturedAt < reservation.UploadExpiresAt
            && string.Equals(statement.Version, media.Version, StringComparison.Ordinal)
            && string.Equals(statement.Sha256, media.Sha256, StringComparison.Ordinal)
            ? statement.CapturedAt : null;
    }
}
