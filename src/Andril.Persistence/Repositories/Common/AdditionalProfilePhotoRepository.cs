using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class AdditionalProfilePhotoRepository(AccountsDbContext db) : IAdditionalProfilePhotoRepository
{
    public async Task<IReadOnlyList<AdditionalProfilePhotoRecord>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Set<AdditionalProfilePhotoRecord>().AsNoTracking()
                .Where(x => x.ProfileId == profileId)
                .OrderBy(x => x.SlotIndex)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<AdditionalProfilePhotoRecord?> GetByProfileAndSlotAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Set<AdditionalProfilePhotoRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProfileId == profileId && x.SlotIndex == slotIndex, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<AdditionalProfilePhotoRecord> UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await using var tx = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.profile_additional_photos (
                    id, profile_id, account_id, module, slot_index,
                    object_key, original_file_name, media_type, file_size_bytes,
                    created_at_utc, updated_at_utc
                )
                VALUES (
                    {record.Id}, {record.ProfileId}, {record.AccountId}, {record.Module}, {record.SlotIndex},
                    {record.ObjectKey}, {record.OriginalFileName}, {record.MediaType}, {record.FileSizeBytes},
                    {record.CreatedAtUtc}, {record.UpdatedAtUtc}
                )
                ON CONFLICT (profile_id, slot_index) DO UPDATE SET
                    object_key = EXCLUDED.object_key,
                    original_file_name = EXCLUDED.original_file_name,
                    media_type = EXCLUDED.media_type,
                    file_size_bytes = EXCLUDED.file_size_bytes,
                    updated_at_utc = GREATEST(profile_additional_photos.updated_at_utc, EXCLUDED.updated_at_utc)
                WHERE profile_additional_photos.account_id = EXCLUDED.account_id
                    AND profile_additional_photos.module = EXCLUDED.module
                """, cancellationToken);

            var result = await db.Set<AdditionalProfilePhotoRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProfileId == record.ProfileId && x.SlotIndex == record.SlotIndex
                    && x.AccountId == record.AccountId && x.Module == record.Module, cancellationToken)
                ?? throw new ProfileAccessDeniedException();

            if (tx is not null) await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<bool> DeleteSlotPhotoAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM andril_identity.profile_additional_photos
                WHERE profile_id = {profileId} AND slot_index = {slotIndex}
                """, cancellationToken);
            return count > 0;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
