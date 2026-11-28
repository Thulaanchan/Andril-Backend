using Andril.API.Authorization.Hobbies;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    [MapCapturePostgresFact]
    public Task RecoveryEvidence_PostgresRequiresOwnedPrivateTombstoneAndExactCompletedGeneration() =>
        WithDatabaseAsync(async connection =>
        {
            await using var db = CreateContext(connection);
            await db.Database.MigrateAsync();
            var store = new PostgresGalleryUploadOperations(new ReceiptContextFactory(connection), Owner, TimeProvider.System);
            var other = new PostgresGalleryUploadOperations(new ReceiptContextFactory(connection), OtherOwner, TimeProvider.System);
            var repository = new GalleryRepository<HobbiesGalleryDbContext>(db);
            var authority = new TestGalleryAuthorization();
            var boundary = authority.CreateBoundary(repository, repository);
            var intent = UploadIntent(Owner);
            var reservation = await store.ReserveAsync(intent, default);
            var evidence = new GalleryUploadObject(reservation.Binding!, "exact-generation");
            var provider = new RecoveryProvider { Evidence = evidence };
            var id = evidence.Binding.Object.MediaId;
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            Assert.True(await store.CompleteAsync(evidence, _ => Task.FromResult(true), default));
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            Assert.Equal(0, provider.Inspections);
            Assert.Equal(GalleryMetadataStatus.Success, (await boundary.DeletePrivateMediaAsync(id, repository)).Status);
            authority.ProfileId = OtherOwner;
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, other, provider)).Status);
            Assert.Equal(0, provider.Inspections);
            authority.ProfileId = Owner;
            provider.Evidence = evidence with { Version = "changed-generation" };
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            provider.Evidence = evidence with { Binding = evidence.Binding with {
                Intent = intent with { Sha256 = new string('B', 64) }
            } };
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            provider.Evidence = evidence;
            var result = await boundary.VerifyRecoveryEvidenceAsync(id, store, provider);
            Assert.Equal(id, result.Value?.MediaId);
            Assert.Equal(GalleryReadOperation.RecoveryEvidence, authority.LastOperation);

            provider.DuringInspection = async () =>
            {
                await db.GalleryMedia.Where(media => media.Id == id)
                    .ExecuteUpdateAsync(update => update.SetProperty(media => media.IsDeleted, false));
            };
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            Assert.Equal(GalleryMetadataStatus.Success, (await boundary.DeletePrivateMediaAsync(id, repository)).Status);
            provider.DuringInspection = () =>
            {
                authority.Allowed = false;
                return Task.CompletedTask;
            };
            Assert.Equal(GalleryMetadataStatus.Denied,
                (await boundary.VerifyRecoveryEvidenceAsync(id, store, provider)).Status);
            authority.Allowed = true;
            provider.DuringInspection = () => throw new InvalidOperationException("synthetic provider detail");
            var failed = await boundary.VerifyRecoveryEvidenceAsync(id, store, provider);
            Assert.Equal(GalleryMetadataStatus.DependencyUnavailable, failed.Status);
            Assert.Null(failed.Value);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => boundary.VerifyRecoveryEvidenceAsync(id, store, provider, cancellation.Token));
            Assert.True((await db.GalleryMedia.AsNoTracking().SingleAsync()).IsDeleted);
        }, initializeSchema: false);

    private sealed class RecoveryProvider : IGalleryPrivateObjectMutations
    {
        internal GalleryUploadObject? Evidence { get; set; }
        internal Func<Task>? DuringInspection { get; set; }
        internal int Inspections { get; private set; }

        public Task<GalleryUploadObject?> CreateOnlyAsync(GalleryUploadBinding binding,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery inspection must never create an object.");

        public async Task<GalleryUploadObject?> InspectAsync(GalleryUploadBinding binding, CancellationToken cancellationToken)
        {
            Inspections++;
            if (DuringInspection is not null)
                await DuringInspection();
            return Evidence;
        }
    }
}
