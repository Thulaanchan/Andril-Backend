namespace Andril.Hobbies.Interfaces;

internal enum GalleryMutationKind { Upload }

internal readonly record struct GalleryOperationKey(
    Guid OwnerProfileId, string Module, GalleryMutationKind Kind, Guid OperationId);

internal sealed record GalleryUploadIntent(Guid OperationId, Guid OwnerProfileId, string Sha256)
{
    internal GalleryOperationKey Key => new(OwnerProfileId, "Hobbies", GalleryMutationKind.Upload, OperationId);

    internal bool IsValid => OperationId != Guid.Empty && OwnerProfileId != Guid.Empty &&
        Sha256 is { Length: 64 } && Sha256.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal sealed record GalleryUploadBinding(GalleryUploadIntent Intent, GalleryPrivateObjectEvidence Object)
{
    internal bool Matches(GalleryUploadIntent expected) =>
        expected.IsValid && Intent == expected && Object.OwnerModuleProfileId == expected.Key.OwnerProfileId &&
        Object.Module == expected.Key.Module && Object.MediaId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(Object.StorageObjectKey);
}

internal sealed record GalleryUploadObject(GalleryUploadBinding Binding, string Version)
{
    internal bool Matches(GalleryUploadBinding expected) =>
        Binding == expected && !string.IsNullOrWhiteSpace(Version);
}

internal enum GalleryUploadReservationState
{
    Conflict,
    Create,
    Inspect,
    Completed
}

internal sealed record GalleryUploadReservation(GalleryUploadReservationState State, GalleryUploadBinding? Binding,
    GalleryUploadObject? Completed = null);

// A concrete store must reserve atomically by owner and operation identity. Only a first reservation
// may permit create. Retrying an unconfirmed reservation may inspect its exact object.
internal interface IGalleryUploadOperations
{
    Task<GalleryUploadReservation> ReserveAsync(GalleryUploadIntent intent, CancellationToken cancellationToken);

    // Persist the immutable binding and receipt in the same transaction. Repeated completion must
    // accept only the identical receipt. Check authorization again before committing.
    Task<bool> CompleteAsync(GalleryUploadObject evidence, Func<CancellationToken, Task<bool>> reauthorize,
        CancellationToken cancellationToken);
}

internal interface IGalleryPrivateObjectMutations
{
    // Both operations must prove the full server-created binding, operation and content digest.
    // Create is conditional on absence and may never overwrite. Null means unconfirmed.
    Task<GalleryUploadObject?> CreateOnlyAsync(GalleryUploadBinding binding, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
    Task<GalleryUploadObject?> InspectAsync(GalleryUploadBinding binding, CancellationToken cancellationToken);
}
