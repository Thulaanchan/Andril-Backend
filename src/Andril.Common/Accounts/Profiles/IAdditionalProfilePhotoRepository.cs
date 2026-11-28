namespace Andril.Common.Accounts.Profiles;

public interface IAdditionalProfilePhotoRepository
{
    Task<IReadOnlyList<AdditionalProfilePhotoRecord>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<AdditionalProfilePhotoRecord?> GetByProfileAndSlotAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default);
    Task<AdditionalProfilePhotoRecord> UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord record, CancellationToken cancellationToken = default);
    Task<bool> DeleteSlotPhotoAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default);
}
