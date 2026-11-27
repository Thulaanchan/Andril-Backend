using System.Data;
using System.Data.Common;
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Hobbies;

internal interface IMapModerationTransactionAuthorization
{
    // True requires the authoritative Hobbies reviewer grant and a revocation
    // fence held by this transaction until commit or rollback.
    Task<bool> CanReviewAsync(DbConnection connection, DbTransaction transaction, Guid reviewer,
        MapModerationScope scope, CancellationToken cancellationToken);
}

internal sealed class DenyAllMapModerationTransactionAuthorization : IMapModerationTransactionAuthorization
{
    public Task<bool> CanReviewAsync(DbConnection connection, DbTransaction transaction, Guid reviewer,
        MapModerationScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

// Unregistered. The shared Admin permission identifier and transaction-aware
// grant reader are external contracts; an unconfigured writer fails closed.
internal sealed class MapModerationRepository(IDbContextFactory<HobbiesMapDbContext> contexts,
    ICurrentUser actor, TimeProvider clock, IMapModerationTransactionAuthorization? authorization = null)
    : IMapModerationRepository
{
    public async Task<MapModerationItem?> FindAsync(MapModerationScope scope, CancellationToken cancellationToken)
    {
        if (authorization is null || !actor.IsAuthenticated || actor.UserId is not Guid reviewer)
            return null;

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await IsAuthorizedAsync(db, tx, reviewer, scope, cancellationToken))
            return null;

        return await db.MapContributions.AsNoTracking()
            .Where(x => x.ContributionId == scope.ContributionId && x.AttractionId == scope.AttractionId)
            .Select(x => new MapModerationItem(scope, x.OwnerHobbiesModuleProfileId, x.CaptureSessionId,
                db.Set<MapModerationReceipt>().Where(r => r.ContributionId == x.ContributionId)
                    .Select(r => (long?)r.Revision).SingleOrDefault() ?? 0,
                !db.Set<MapModerationReceipt>().Any(r => r.ContributionId == x.ContributionId)))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<MapModerationOutcome> DecideAsync(MapModerationScope scope, MapContributionReviewRequest request,
        VerifiedMapMedia? approvalEvidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authorization is null || !actor.IsAuthenticated || actor.UserId is not Guid reviewer
            || reviewer == Guid.Empty || scope.ContributionId != request.ContributionId
            || !string.Equals(request.ReviewerSubject, reviewer.ToString("D"), StringComparison.Ordinal))
            return MapModerationOutcome.Denied;

        var committing = false;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await IsAuthorizedAsync(db, tx, reviewer, scope, cancellationToken))
                return MapModerationOutcome.Denied;

            var contribution = await db.MapContributions.FromSqlInterpolated($"""
                SELECT * FROM andril_hobbies.map_contributions
                WHERE contribution_id = {scope.ContributionId} AND attraction_id = {scope.AttractionId}
                FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (contribution is null || !actor.IsAuthenticated || actor.UserId != reviewer)
                return MapModerationOutcome.Denied;

            var existing = await db.Set<MapModerationReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ContributionId == scope.ContributionId, cancellationToken);
            if (existing is not null)
                return existing.DecisionId == request.DecisionId && existing.ReviewerSubject == reviewer
                    && existing.Action == request.Action && existing.ExpectedRevision == request.ExpectedRevision
                    ? MapModerationOutcome.Replay : MapModerationOutcome.Conflict;

            if (request.ExpectedRevision != 0)
                return MapModerationOutcome.Conflict;

            Guid? mediaId = null;
            if (request.Action == MapContributionReviewAction.Approve)
            {
                if (approvalEvidence is null || !await MatchesMediaAsync(db, contribution, approvalEvidence, cancellationToken))
                    return MapModerationOutcome.Ineligible;
                mediaId = approvalEvidence.MediaId;
            }

            var now = clock.GetUtcNow().ToUniversalTime();
            var decision = MapContributionModeration.CreatePending(contribution, 0).Decide(request, now);
            if (!actor.IsAuthenticated || actor.UserId != reviewer)
                return MapModerationOutcome.Denied;
            db.Add(new MapModerationReceipt
            {
                DecisionId = request.DecisionId, ContributionId = contribution.ContributionId,
                AttractionId = contribution.AttractionId, ProfileId = contribution.OwnerHobbiesModuleProfileId,
                SessionId = contribution.CaptureSessionId, ReviewerSubject = reviewer, Action = request.Action,
                ExpectedRevision = request.ExpectedRevision, Revision = decision.Revision,
                DecidedAt = decision.Decision!.DecidedAtUtc, MediaId = mediaId
            });
            db.Add(new MapModerationAudit { DecisionId = request.DecisionId });
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            committing = true;
            await tx.CommitAsync(cancellationToken);
            return MapModerationOutcome.Applied;
        }
        catch (DbUpdateException error) when (!committing && error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_map_moderation_receipts" })
        {
            return MapModerationOutcome.Conflict;
        }
        catch (Exception error) when (committing && error is (NpgsqlException or OperationCanceledException or TimeoutException))
        {
            return MapModerationOutcome.OutcomeUnknown;
        }
    }

    private async Task<bool> IsAuthorizedAsync(HobbiesMapDbContext db, IDbContextTransaction tx, Guid reviewer,
        MapModerationScope scope, CancellationToken cancellationToken) =>
        reviewer != Guid.Empty && authorization is not null
        && await authorization.CanReviewAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            reviewer, scope, cancellationToken)
        && actor.IsAuthenticated && actor.UserId == reviewer;

    private static Task<bool> MatchesMediaAsync(HobbiesMapDbContext db, MapContribution contribution,
        VerifiedMapMedia evidence, CancellationToken cancellationToken) =>
        db.Set<MapPrivateMediaRow>().AnyAsync(media => media.MediaId == evidence.MediaId
            && media.ProfileId == contribution.OwnerHobbiesModuleProfileId
            && media.AttractionId == contribution.AttractionId && media.SessionId == contribution.CaptureSessionId
            && media.FinalizedAt != null && media.CleanupClaim == null
            && media.PrivateReference == contribution.PrivatePhotoStorageObjectKey
            && media.PrivateReference == evidence.PrivateReference && media.Version == evidence.Version
            && media.Sha256 == evidence.Sha256, cancellationToken);
}
