using System.Transactions;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andril.Persistence.Repositories.Hobbies;

// Controlled composition only. The owner must come from authenticated server scope.
internal sealed class PostgresGalleryUploadOperations(
    IDbContextFactory<HobbiesGalleryDbContext> contexts, Guid owner, TimeProvider clock)
    : IGalleryUploadOperations, IGalleryRecoveryEvidenceReader
{
    public async Task<GalleryUploadReceipt?> FindOwnedTombstoneAsync(Guid mediaId, Guid ownerProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mediaId == Guid.Empty || ownerProfileId == Guid.Empty || ownerProfileId != owner)
            return null;

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (from media in db.GalleryMedia
                         join receipt in db.Set<GalleryUploadOperation>() on media.Id equals receipt.MediaId
                         where media.Id == mediaId && media.OwnerModuleProfileId == owner &&
                               media.Module == "Hobbies" && media.IsPrivate && media.IsDeleted &&
                               media.ProviderGeneration != null && receipt.OwnerId == owner &&
                               receipt.Module == media.Module && receipt.Kind == (short)GalleryMutationKind.Upload &&
                               receipt.State == 2 && receipt.PrivateObjectKey == media.StorageObjectKey &&
                               receipt.ProviderGeneration == media.ProviderGeneration
                         select receipt).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Read(row);
    }

    public async Task<GalleryUploadReservation> ReserveAsync(GalleryUploadIntent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (owner != intent.OwnerProfileId || !intent.IsValid)
            return new(GalleryUploadReservationState.Conflict, null);

        await using var db = await contexts.CreateDbContextAsync(token);
        EnsureOwnTransaction(db);
        var key = intent.Key;
        var mediaId = Guid.NewGuid();
        var objectKey = $"gallery/{owner:N}/{mediaId:N}.jpg";
        var now = clock.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_hobbies."GalleryUploadOperations"
              (owner_module_profile_id, module, operation_kind, operation_id, fingerprint_sha256,
               media_id, private_object_key, state, revision, created_at_utc, updated_at_utc)
            VALUES ({owner}, {key.Module}, {(short)key.Kind}, {key.OperationId}, {intent.Sha256},
                    {mediaId}, {objectKey}, 0, 0, {now}, {now})
            ON CONFLICT (owner_module_profile_id, module, operation_kind, operation_id) DO NOTHING
            """, token);
        if (inserted == 1)
            return new(GalleryUploadReservationState.Create,
                new(intent, new(mediaId, owner, key.Module, objectKey)));

        var row = await Find(db, key).SingleAsync(token);
        return Read(row).Replay(intent);
    }

    public async Task<bool> CompleteAsync(GalleryUploadObject evidence, Func<CancellationToken, Task<bool>> reauthorize,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var intent = evidence.Binding.Intent;
        if (owner != intent.OwnerProfileId || !evidence.Binding.Matches(intent))
            return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        EnsureOwnTransaction(db);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var query = Find(db, intent.Key);
        var row = await query.SingleOrDefaultAsync(token);
        if (row is null)
            return false;
        var receipt = Read(row);
        var verified = receipt.Verify(row.Revision, evidence);
        if (verified is null || !await reauthorize(token))
            return false;
        if (receipt.Completed)
            return true;
        var completed = verified.Complete(verified.Revision, evidence)!;
        var now = clock.GetUtcNow();
        if (now < row.UpdatedAtUtc)
            now = row.UpdatedAtUtc;
        if (row.State == 0 && await query.Where(item => item.Revision == row.Revision)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.State, (short)1)
                    .SetProperty(item => item.Revision, verified.Revision)
                    .SetProperty(item => item.ProviderGeneration, evidence.Version)
                    .SetProperty(item => item.UpdatedAtUtc, now), token) != 1)
            return false;

        var binding = receipt.Binding.Object;
        db.GalleryMedia.Add(new GalleryMedia
        {
            Id = binding.MediaId,
            OwnerModuleProfileId = binding.OwnerModuleProfileId,
            Module = binding.Module,
            StorageObjectKey = binding.StorageObjectKey,
            ProviderGeneration = evidence.Version,
            CreatedAtUtc = now
        });
        if (!await reauthorize(token))
            return false;
        await db.SaveChangesAsync(token);
        if (await query.Where(item => item.Revision == verified.Revision && item.ProviderGeneration == evidence.Version)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.State, (short)2)
                    .SetProperty(item => item.Revision, completed.Revision)
                    .SetProperty(item => item.UpdatedAtUtc, now), token) != 1 ||
            !await reauthorize(token))
            return false;

        await transaction.CommitAsync(token);
        return true;
    }

    private static IQueryable<GalleryUploadOperation> Find(HobbiesGalleryDbContext db, GalleryOperationKey key) =>
        db.Set<GalleryUploadOperation>().AsNoTracking().Where(row => row.OwnerId == key.OwnerProfileId &&
            row.Module == key.Module && row.Kind == (short)key.Kind && row.OperationId == key.OperationId);

    private static GalleryUploadReceipt Read(GalleryUploadOperation row)
    {
        var intent = new GalleryUploadIntent(row.OperationId, row.OwnerId, row.Fingerprint);
        var binding = new GalleryUploadBinding(intent, new(row.MediaId, row.OwnerId, row.Module, row.PrivateObjectKey));
        var reserved = GalleryUploadReceipt.Reserve(binding) ?? throw new InvalidOperationException("Invalid Gallery receipt.");
        if (row.State == 0)
            return reserved;
        var evidence = new GalleryUploadObject(binding, row.ProviderGeneration!);
        var verified = reserved.Verify(reserved.Revision, evidence) ?? throw new InvalidOperationException("Invalid Gallery evidence.");
        return row.State == 1 ? verified : verified.Complete(verified.Revision, evidence)!;
    }

    private static void EnsureOwnTransaction(HobbiesGalleryDbContext db)
    {
        if (Transaction.Current is not null || db.Database.CurrentTransaction is not null ||
            db.Database.GetEnlistedTransaction() is not null || ExecutionStrategy.Current?.RetriesOnFailure == true ||
            db.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Gallery receipts require a dedicated non-retrying database context.");
    }
}
