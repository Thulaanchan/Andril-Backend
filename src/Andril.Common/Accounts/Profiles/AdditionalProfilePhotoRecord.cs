using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed class AdditionalProfilePhotoRecord
{
    private AdditionalProfilePhotoRecord() { }

    public Guid Id { get; private set; }
    public Guid ProfileId { get; private set; }
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = string.Empty;
    public int SlotIndex { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;
    public string? OriginalFileName { get; private set; }
    public string MediaType { get; private set; } = "image/jpeg";
    public int FileSizeBytes { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public const int MinSlotIndex = 1;
    public const int MaxSlotIndex = 3;

    public static AdditionalProfilePhotoRecord Create(
        Guid profileId,
        Guid accountId,
        string module,
        int slotIndex,
        string objectKey,
        string? originalFileName,
        string mediaType,
        int fileSizeBytes,
        DateTimeOffset now)
    {
        if (profileId == Guid.Empty || accountId == Guid.Empty)
            throw new ProfileValidationException();
        if (slotIndex is < MinSlotIndex or > MaxSlotIndex)
            throw new InvalidPhotoSlotException($"Slot index must be between {MinSlotIndex} and {MaxSlotIndex}. Received: {slotIndex}");
        ValidateMetadata(objectKey, originalFileName, mediaType, fileSizeBytes);

        if (!StarterModules.StarterModules.IsValid(module)) throw new ProfileValidationException();
        var utc = now.ToUniversalTime();
        return new AdditionalProfilePhotoRecord
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            AccountId = accountId,
            Module = module.Trim().ToLowerInvariant(),
            SlotIndex = slotIndex,
            ObjectKey = objectKey.Trim(),
            OriginalFileName = originalFileName?.Trim(),
            MediaType = mediaType.Trim(),
            FileSizeBytes = fileSizeBytes,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    public void Replace(string objectKey, string? originalFileName, string mediaType, int fileSizeBytes, DateTimeOffset now)
    {
        ValidateMetadata(objectKey, originalFileName, mediaType, fileSizeBytes);

        ObjectKey = objectKey.Trim();
        OriginalFileName = originalFileName?.Trim();
        MediaType = mediaType.Trim();
        FileSizeBytes = fileSizeBytes;
        UpdatedAtUtc = now.ToUniversalTime();
    }
    private static void ValidateMetadata(string objectKey, string? originalFileName, string mediaType, int fileSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Trim().Length > 128 || objectKey.Any(char.IsControl)
            || originalFileName?.Trim().Length > 256 || originalFileName?.Any(char.IsControl) == true
            || mediaType is not ("image/jpeg" or "image/png") || fileSizeBytes is < 1 or > 5242880)
            throw new ProfileValidationException();
    }

}

public sealed record AdditionalPhotoSlotDto(
    [property: JsonPropertyName("slotIndex")] int SlotIndex,
    [property: JsonPropertyName("photoId")] Guid? PhotoId,
    [property: JsonPropertyName("hasPhoto")] bool HasPhoto,
    [property: JsonPropertyName("objectKey")] string? ObjectKey,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset? UpdatedAtUtc
);

public sealed record AdditionalPhotosListDto(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("slots")] IReadOnlyList<AdditionalPhotoSlotDto> Slots
);

public sealed class InvalidPhotoSlotException : Exception
{
    public InvalidPhotoSlotException(string message) : base(message) { }
}

public sealed class ProfileBioTooLongException : Exception
{
    public ProfileBioTooLongException(string message) : base(message) { }
}

public sealed class SocialProfileRequiresMainPhotoException : Exception
{
    public SocialProfileRequiresMainPhotoException(string message) : base(message) { }
}
