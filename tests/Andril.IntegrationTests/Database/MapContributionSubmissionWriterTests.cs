using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class MapContributionSubmissionWriterTests
{
    [MapCapturePostgresFact]
    public Task LockedSessionCancellationAndRollbackLeaveIndependentSessionAvailable() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var independent = await PrepareAsync(factory);
            var writer = new MapContributionSubmissionWriter(factory);
            await using var blocker = factory.CreateDbContext();
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions
                WHERE session_id = {prepared.Contribution.CaptureSessionId} FOR UPDATE
                """);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SubmitAsync(prepared, canceled.Token));
            using var waitingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.SubmitAsync(prepared, waitingCancellation.Token));
            var waiting = writer.SubmitAsync(prepared);
            var separate = await writer.SubmitAsync(independent).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(MapSubmissionOutcome.Created, separate.Outcome);
            Assert.False(waiting.IsCompleted);
            await transaction.RollbackAsync();
            Assert.Equal(MapSubmissionOutcome.Created, (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        });

    [MapCapturePostgresFact]
    public Task ParallelRequestsAndRetriesReturnOnlyTheAuthorizedExistingRecord() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var first = await PrepareAsync(factory);
            var writer = new MapContributionSubmissionWriter(factory);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.SubmitAsync(first)));
            Assert.Single(results, result => result.Outcome == MapSubmissionOutcome.Created);
            Assert.Equal(7, results.Count(result => result.Outcome == MapSubmissionOutcome.ExistingAuthorized));
            Assert.All(results, result => Assert.Equal(first.Contribution.ContributionId, result.ContributionId));
            await using var probe = factory.CreateDbContext();
            Assert.Equal(1, await probe.MapContributions.CountAsync());
            Assert.Equal(MapContributionModerationStatus.PendingReview,
                (await probe.MapContributions.SingleAsync()).ModerationStatus);
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            Assert.Equal(MapSubmissionOutcome.ExistingAuthorized, (await writer.SubmitAsync(first)).Outcome);
        });

    [MapCapturePostgresFact]
    public Task ChangedSessionScopeTimingOrCanonicalCoordinatesDenyWithoutWriting() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            foreach (var column in new[] { "owner_hobbies_module_profile_id", "attraction_id", "completed_at_utc", "started_at_utc", "missing", "gps" })
            {
                var prepared = await PrepareAsync(factory);
                var row = prepared.Contribution;
                await using var probe = factory.CreateDbContext();
                if (column == "missing")
                    await probe.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id = {row.CaptureSessionId}");
                else if (column == "gps")
                    await probe.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_hobbies.map_attractions SET latitude = 45 WHERE attraction_id = {row.AttractionId}");
                else
                {
                    var value = column == "completed_at_utc" ? "NULL"
                        : column == "started_at_utc" ? "started_at_utc - interval '1 second'"
                        : $"'{Guid.NewGuid()}'::uuid";
                    // Identifiers and expressions come only from the fixed test cases above.
                    await probe.Database.ExecuteSqlRawAsync(
                        $"UPDATE andril_hobbies.map_contribution_capture_sessions SET {column} = {value} WHERE session_id = '{row.CaptureSessionId}'");
                }
                var result = await new MapContributionSubmissionWriter(factory).SubmitAsync(prepared);
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), result);
                Assert.Empty(await probe.MapContributions.ToListAsync());
            }
        });

    [MapCapturePostgresFact]
    public Task FailedInsertRollsBackAndCommitAcknowledgementLossIsUnknown() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var failing = new Contexts(connection, new FailInsert());
            Assert.Equal(MapSubmissionOutcome.InfrastructureFailure,
                (await new MapContributionSubmissionWriter(failing).SubmitAsync(prepared)).Outcome);
            await using var probe = factory.CreateDbContext();
            Assert.Empty(await probe.MapContributions.ToListAsync());
            var ambiguous = new Contexts(connection, new LoseAcknowledgement());
            Assert.Equal(MapSubmissionOutcome.OutcomeUnknown,
                (await new MapContributionSubmissionWriter(ambiguous).SubmitAsync(prepared)).Outcome);
            Assert.Equal(1, await probe.MapContributions.CountAsync());
            Assert.Equal(MapSubmissionOutcome.ExistingAuthorized,
                (await new MapContributionSubmissionWriter(factory).SubmitAsync(prepared)).Outcome);
        });

    private sealed class FailInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new NpgsqlException("Synthetic insert failure");
    }

    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            throw new NpgsqlException("Synthetic acknowledgement loss");
    }

    private static async Task<MapContributionSubmissionPreflight.PreparedSubmission> PrepareAsync(Contexts factory)
    {
        await using var context = factory.CreateDbContext();
        var owner = Guid.NewGuid();
        var attraction = MapAttraction.Create(Guid.NewGuid(), "Synthetic", 0, 0);
        var session = MapContributionCaptureSession.Create(Guid.NewGuid(), owner, attraction.AttractionId, DateTimeOffset.UtcNow.AddSeconds(-10));
        session.Complete(session.StartedAtUtc.AddSeconds(5));
        context.MapAttractions.Add(attraction);
        context.CaptureSessions.Add(session);
        await context.SaveChangesAsync();
        var ports = new TrustedTestPorts(owner);
        var preflight = new MapContributionSubmissionPreflight(ports,
            new MapContributionCaptureSessionRepository<HobbiesMapDbContext>(context),
            new MapAttractionRepository<HobbiesMapDbContext>(context), ports);
        return Assert.IsType<MapContributionSubmissionPreflight.PreparedSubmission>(
            await preflight.PrepareAsync(attraction.AttractionId, session.SessionId,
                new MapContributionLocationEvidence(0, 0, 90, 90, 5, null, null), "private/photo", "Description"));
    }

    private sealed class TrustedTestPorts(Guid owner) : IMapCaptureSessionAccessReader, IMapContributionPhotoEvidenceReader
    {
        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(owner);
        public Task<MapContributionPhotoVerificationResult> VerifyAsync(Guid profile, Guid attraction,
            Guid session, string? key, CancellationToken cancellationToken) =>
            Task.FromResult(MapContributionPhotoVerificationResult.Verified);
    }

    private sealed class Contexts(string connection, IInterceptor? interceptor = null) : IDbContextFactory<HobbiesMapDbContext>
    {
        public HobbiesMapDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return new(options.Options);
        }
    }
}
