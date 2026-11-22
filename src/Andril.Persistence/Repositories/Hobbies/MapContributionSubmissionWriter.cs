using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Andril.IntegrationTests")]

namespace Andril.Persistence.Repositories.Hobbies;

// Deliberately unregistered. Production photo and current authorization contracts
// must be approved before exposing this writer through an application entry point.
internal sealed class MapContributionSubmissionWriter(
    IDbContextFactory<HobbiesMapDbContext> contexts,
    TimeProvider? timeProvider = null) : IMapContributionSubmissionWriter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public async Task<MapSubmissionResult> SubmitAsync(
        MapContributionSubmissionPreflight.PreparedSubmission prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        var contribution = prepared.Contribution;
        var committing = false;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var session = await context.CaptureSessions.FromSqlInterpolated($"""
                SELECT * FROM andril_hobbies.map_contribution_capture_sessions
                WHERE session_id = {contribution.CaptureSessionId}
                  AND owner_hobbies_module_profile_id = {contribution.OwnerHobbiesModuleProfileId}
                  AND attraction_id = {contribution.AttractionId}
                FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (session is null || session.CompletedAtUtc is null)
                return new(MapSubmissionOutcome.Denied);

            var attraction = await context.MapAttractions.AsNoTracking()
                .SingleOrDefaultAsync(row => row.AttractionId == contribution.AttractionId, cancellationToken);
            var validated = MapContributionEvidenceValidator.Validate(
                attraction, session, contribution.OwnerHobbiesModuleProfileId, contribution.LocationEvidence);
            if (!validated.PassesChecks || validated.Evidence != contribution.LocationEvidence)
                return new(MapSubmissionOutcome.Denied);

            var existing = await context.MapContributions.AsNoTracking().SingleOrDefaultAsync(
                row => row.CaptureSessionId == contribution.CaptureSessionId
                    && row.OwnerHobbiesModuleProfileId == contribution.OwnerHobbiesModuleProfileId
                    && row.AttractionId == contribution.AttractionId, cancellationToken);
            if (existing is not null)
                return new(MapSubmissionOutcome.ExistingAuthorized, existing.ContributionId);

            if (!MapContributionSubmissionWindow.Allows(session, _timeProvider.GetUtcNow()))
                return new(MapSubmissionOutcome.Denied);

            context.MapContributions.Add(contribution);
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck after database work. Returning disposes and rolls back the transaction.
            // This is a pre-commit gate, not a guarantee of the database commit's completion time.
            if (!MapContributionSubmissionWindow.Allows(session, _timeProvider.GetUtcNow()))
                return new(MapSubmissionOutcome.Denied);
            committing = true;
            await transaction.CommitAsync(cancellationToken);
            return new(MapSubmissionOutcome.Created, contribution.ContributionId);
        }
        catch (DbUpdateException error) when (!committing
            && error.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                SchemaName: "andril_hobbies",
                TableName: "map_contributions",
                ConstraintName: "ux_map_contributions_capture_session_id"
            })
        {
            return await FindExistingAsync(contribution, cancellationToken);
        }
        catch (OperationCanceledException) when (!committing)
        {
            throw;
        }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException or OperationCanceledException)
        {
            return new(committing ? MapSubmissionOutcome.OutcomeUnknown : MapSubmissionOutcome.InfrastructureFailure);
        }
    }

    private async Task<MapSubmissionResult> FindExistingAsync(
        MapContribution contribution, CancellationToken cancellationToken)
    {
        try
        {
            // The failed transaction and connection have been disposed before this query.
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var existing = await context.MapContributions.AsNoTracking().SingleOrDefaultAsync(
                row => row.CaptureSessionId == contribution.CaptureSessionId
                    && row.OwnerHobbiesModuleProfileId == contribution.OwnerHobbiesModuleProfileId
                    && row.AttractionId == contribution.AttractionId, cancellationToken);
            return existing is null
                ? new(MapSubmissionOutcome.Denied)
                : new(MapSubmissionOutcome.ExistingAuthorized, existing.ContributionId);
        }
        catch (NpgsqlException)
        {
            return new(MapSubmissionOutcome.InfrastructureFailure);
        }
    }
}
