using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapContributionSubmissionWriterTests
{
    [MapCapturePostgresFact]
    public Task Service_CommitsOnePendingContributionAndReconcilesOnlyAuthorizedRetries() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var seed = (await PrepareAsync(contexts)).Contribution;
            await using var reads = contexts.CreateDbContext();
            var service = Service(reads, contexts, seed);
            var first = await Submit(service, seed);
            Assert.Equal(MapSubmissionOutcome.Created, first.Outcome);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, first.ContributionId),
                await Submit(service, seed));

            var foreign = new TrustedTestPorts(Guid.NewGuid());
            var denied = new MapContributionSubmissionService(new(foreign,
                new MapContributionCaptureSessionRepository<HobbiesMapDbContext>(reads),
                new MapAttractionRepository<HobbiesMapDbContext>(reads), foreign), new MapContributionSubmissionWriter(contexts, new TrustedSubmissionAuthorization(seed.OwnerHobbiesModuleProfileId)));
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await Submit(denied, seed));
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied),
                await Submit(Service(reads, contexts, seed, new DenyAllMapContributionPhotoEvidenceReader()), seed));

            await using var probe = contexts.CreateDbContext();
            var saved = Assert.Single(await probe.MapContributions.AsNoTracking().ToListAsync());
            Assert.Equal(first.ContributionId, saved.ContributionId);
            Assert.Equal(seed.OwnerHobbiesModuleProfileId, saved.OwnerHobbiesModuleProfileId);
            Assert.Equal(MapContributionModerationStatus.PendingReview, saved.ModerationStatus);
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task Service_RolledBackInsertReturnsInfrastructureFailureAndExplicitRetryCanCommit() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var seed = (await PrepareAsync(contexts)).Contribution;
            await using var reads = contexts.CreateDbContext();
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure),
                await Submit(Service(reads, new Contexts(connection, new FailInsert()), seed), seed));
            await using var probe = contexts.CreateDbContext();
            Assert.Empty(await probe.MapContributions.ToListAsync());
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            Assert.Equal(MapSubmissionOutcome.Created, (await Submit(Service(reads, contexts, seed), seed)).Outcome);
            Assert.Single(await probe.MapContributions.AsNoTracking().ToListAsync());
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task Service_LostCommitAcknowledgementRemainsUnknownUntilExplicitAuthorizedRetry() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var contexts = new Contexts(connection);
            var seed = (await PrepareAsync(contexts)).Contribution;
            await using var reads = contexts.CreateDbContext();
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown),
                await Submit(Service(reads, new Contexts(connection, new LoseAcknowledgement()), seed), seed));
            await using var probe = contexts.CreateDbContext();
            var committed = Assert.Single(await probe.MapContributions.AsNoTracking().ToListAsync());
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committed.ContributionId),
                await Submit(Service(reads, contexts, seed), seed));
            Assert.Single(await probe.MapContributions.AsNoTracking().ToListAsync());
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
        });

    private static MapContributionSubmissionService Service(HobbiesMapDbContext reads, Contexts contexts,
        MapContribution seed, IMapContributionPhotoEvidenceReader? photo = null)
    {
        // Synthetic trusted evidence only; the production reader remains deny-all.
        var ports = new TrustedTestPorts(seed.OwnerHobbiesModuleProfileId);
        return new(new(ports, new MapContributionCaptureSessionRepository<HobbiesMapDbContext>(reads),
            new MapAttractionRepository<HobbiesMapDbContext>(reads), photo ?? ports), new MapContributionSubmissionWriter(contexts, new TrustedSubmissionAuthorization(seed.OwnerHobbiesModuleProfileId)));
    }

    private static Task<MapSubmissionResult> Submit(MapContributionSubmissionService service, MapContribution seed) =>
        service.SubmitAsync(seed.AttractionId, seed.CaptureSessionId, seed.LocationEvidence,
            seed.PrivatePhotoStorageObjectKey, seed.Description);
}
