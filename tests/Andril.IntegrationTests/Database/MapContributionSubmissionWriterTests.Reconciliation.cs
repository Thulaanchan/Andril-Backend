using System.Text.Json;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapContributionSubmissionWriterTests
{
    [MapCapturePostgresFact]
    public Task Reconciliation_LostAcknowledgementUsesExactCommittedEvidenceWithoutAnotherWrite() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var seed = (await PrepareAsync(contexts)).Contribution;
            await using var reads = contexts.CreateDbContext();
            var attempt = await Service(reads, new Contexts(connection, new LoseAcknowledgement()), seed)
                .SubmitAttemptAsync(seed.AttractionId, seed.CaptureSessionId, seed.LocationEvidence,
                    seed.PrivatePhotoStorageObjectKey, seed.Description);
            Assert.Equal(MapSubmissionOutcome.OutcomeUnknown, attempt.Result.Outcome);
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { Enlist = false }.ConnectionString);
            var actor = new ReconciliationActor(new(seed.OwnerHobbiesModuleProfileId, "Hobbies", true));
            var service = new MapContributionSubmissionReconciliationService(new MapContributionCommittedReader(source), actor);
            var committed = Assert.Single(await reads.MapContributions.AsNoTracking().ToListAsync());
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committed.ContributionId),
                await service.ReconcileAsync(attempt.OriginalEvidence));
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committed.ContributionId),
                await service.ReconcileAsync(attempt.OriginalEvidence));
            actor.Value = actor.Value! with { ModuleProfileId = Guid.NewGuid() };
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await service.ReconcileAsync(attempt.OriginalEvidence));
            actor.Value = new(seed.OwnerHobbiesModuleProfileId, "Social", true);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await service.ReconcileAsync(attempt.OriginalEvidence));
            Assert.Single(await reads.MapContributions.AsNoTracking().ToListAsync());
            Assert.Empty(await reads.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task Reconciliation_UncommittedAndRolledBackEvidenceRemainUnknownWithoutWriting() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var prepared = await PrepareAsync(contexts);
            var row = prepared.Contribution;
            var evidence = MapSubmissionOriginalEvidence.Capture(prepared);
            await using var writer = contexts.CreateDbContext();
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { Enlist = false }.ConnectionString);
            var service = new MapContributionSubmissionReconciliationService(new MapContributionCommittedReader(source),
                new ReconciliationActor(new(row.OwnerHobbiesModuleProfileId, "Hobbies", true)));
            await using (var transaction = await writer.Database.BeginTransactionAsync())
            {
                writer.MapContributions.Add(row);
                await writer.SaveChangesAsync();
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), await service.ReconcileAsync(evidence));
                await transaction.RollbackAsync();
            }
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), await service.ReconcileAsync(evidence));
            await using var probe = contexts.CreateDbContext();
            Assert.Empty(await probe.MapContributions.ToListAsync());
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task Reconciliation_ChangedPersistedRequestIsDeniedAndUnavailableDatabaseStaysUnknown() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var prepared = await PrepareAsync(contexts);
            var row = prepared.Contribution;
            var evidence = MapSubmissionOriginalEvidence.Capture(prepared);
            Assert.Equal(MapSubmissionOutcome.Created, (await new MapContributionSubmissionWriter(contexts).SubmitAsync(prepared)).Outcome);
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { Enlist = false }.ConnectionString);
            var actor = new ReconciliationActor(new(row.OwnerHobbiesModuleProfileId, "Hobbies", true));
            var service = new MapContributionSubmissionReconciliationService(new MapContributionCommittedReader(source), actor);
            await using var context = contexts.CreateDbContext();
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_hobbies.map_contributions SET description = {"Changed private payload"}");
            var denied = await service.ReconcileAsync(evidence);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), denied);
            Assert.DoesNotContain(row.PrivatePhotoStorageObjectKey, JsonSerializer.Serialize(denied));
            var settings = new NpgsqlConnectionStringBuilder(connection) { Database = "missing_" + Guid.NewGuid().ToString("N"), Enlist = false };
            await using var unavailable = NpgsqlDataSource.Create(settings.ConnectionString);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown),
                await new MapContributionSubmissionReconciliationService(new MapContributionCommittedReader(unavailable), actor).ReconcileAsync(evidence));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcileAsync(evidence, cancellation.Token));
            Assert.Single(await context.MapContributions.AsNoTracking().ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        });

    private sealed class ReconciliationActor(GalleryActor? value) : IGalleryActorContext
    {
        internal GalleryActor? Value { get; set; } = value;
        public GalleryActor? Current => Value;
    }
}
