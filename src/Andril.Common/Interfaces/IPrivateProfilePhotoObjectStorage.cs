using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;

namespace Andril.Common.Interfaces;

public enum PhotoObjectOutcome
{
    Success, Missing, Conflict, TransientFailure, PermanentFailure, Ambiguous, IntegrityFailure,
    MissingBucket, InvalidRequest, InvalidObjectPath, AuthenticationFailure, PermissionFailure, RateLimited, InvalidResponse
}

public sealed record PhotoObjectRead(PhotoObjectOutcome Outcome, byte[]? Bytes = null);

public interface IPrivateProfilePhotoObjectStorage
{
    bool IsConfigured { get; }
    Task<PhotoObjectOutcome> UploadAsync(StoredProfilePhoto expected, byte[] jpeg, CancellationToken cancellationToken);
    // A successful read verifies exact length, JPEG media type and the persisted SHA-256.
    Task<PhotoObjectRead> ReadAsync(StoredProfilePhoto expected, CancellationToken cancellationToken);
    Task<PhotoObjectOutcome> DeleteAsync(PhotoObjectAddress address, CancellationToken cancellationToken);
}
