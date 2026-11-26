using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Hobbies;

internal sealed class MapPrivateMediaRow
{
    public Guid MediaId { get; init; }
    public Guid SubjectId { get; init; }
    public Guid ProfileId { get; init; }
    public Guid AttractionId { get; init; }
    public Guid SessionId { get; init; }
    public Guid Challenge { get; init; }
    public DateTimeOffset ReservedAt { get; init; }
    public DateTimeOffset UploadExpiresAt { get; init; }
    public string? PrivateReference { get; init; }
    public string? Version { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public Guid? CleanupClaim { get; init; }
    public DateTimeOffset? CleanedAt { get; init; }

    internal MapMediaReservation Reservation() =>
        new(new(SubjectId, ProfileId, AttractionId, SessionId), MediaId, Challenge, ReservedAt, UploadExpiresAt);
}

internal sealed class MapPrivateMediaStore(IDbContextFactory<HobbiesMapDbContext> contexts,
    ICurrentUser actor, IMapSubmissionTransactionAuthorization authorization, TimeProvider clock)
    : IMapPrivateMediaStore
{
    public async Task<bool> TryReserveAsync(MapMediaReservation reservation, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var scope = reservation.Scope;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizesAsync(db, transaction, scope, cancellationToken))
            return false;
        db.Set<MapPrivateMediaRow>().Add(new()
        {
            MediaId = reservation.MediaId, SubjectId = scope.SubjectId, ProfileId = scope.ProfileId,
            AttractionId = scope.AttractionId, SessionId = scope.SessionId, Challenge = reservation.CaptureChallenge,
            ReservedAt = reservation.ReservedAt, UploadExpiresAt = reservation.UploadExpiresAt
        });
        try
        {
            if (await db.SaveChangesAsync(cancellationToken) != 1)
                return false;
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_map_private_media_session" })
        {
            return false;
        }
    }

    public async Task<MapMediaReservation?> FindReservationAsync(MapMediaScope scope, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await Scoped(db, scope).AsNoTracking().SingleOrDefaultAsync(x => x.CleanupClaim == null, cancellationToken);
        return row?.Reservation();
    }

    public async Task<FinalizedMapMedia?> FindFinalizedAsync(MapMediaScope scope, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await Scoped(db, scope).AsNoTracking()
            .SingleOrDefaultAsync(x => x.FinalizedAt != null && x.CleanupClaim == null, cancellationToken);
        return row is null ? null : new(row.Reservation(),
            new(row.MediaId, row.PrivateReference!, row.Version!, row.Sha256!), row.CapturedAt!.Value);
    }

    public async Task<bool> TryFinalizeAsync(FinalizedMapMedia evidence, CancellationToken cancellationToken)
    {
        var reservation = evidence.Reservation;
        var media = evidence.Media;
        if (media.MediaId != reservation.MediaId)
            return false;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizesAsync(db, transaction, reservation.Scope, cancellationToken))
            return false;
        var scope = reservation.Scope;
        var session = await db.CaptureSessions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SessionId == scope.SessionId && x.OwnerHobbiesModuleProfileId == scope.ProfileId
                && x.AttractionId == scope.AttractionId, cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        if (session?.CompletedAtUtc is not { } completed
            || !MapContributionSubmissionWindow.Allows(session, now)
            || completed - session.StartedAtUtc > MapContributionLocationPolicy.MaximumCaptureDuration
            || evidence.CapturedAt < session.StartedAtUtc || evidence.CapturedAt > completed
            || evidence.CapturedAt < reservation.ReservedAt || evidence.CapturedAt >= reservation.UploadExpiresAt)
            return false;
        var updated = await Scoped(db, reservation.Scope)
            .Where(x => x.MediaId == reservation.MediaId && x.Challenge == reservation.CaptureChallenge
                && x.ReservedAt == reservation.ReservedAt && x.UploadExpiresAt == reservation.UploadExpiresAt
                && x.FinalizedAt == null && x.CleanupClaim == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.PrivateReference, media.PrivateReference)
                .SetProperty(x => x.Version, media.Version)
                .SetProperty(x => x.Sha256, media.Sha256)
                .SetProperty(x => x.CapturedAt, evidence.CapturedAt)
                .SetProperty(x => x.FinalizedAt, now), cancellationToken);
        if (updated != 1)
            return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> AuthorizesAsync(HobbiesMapDbContext db, IDbContextTransaction transaction,
        MapMediaScope scope, CancellationToken cancellationToken)
    {
        if (!actor.IsAuthenticated || actor.UserId != scope.SubjectId
            || await authorization.ResolveActiveHobbiesOwnerAsync(db.Database.GetDbConnection(),
                transaction.GetDbTransaction(), cancellationToken) != scope.ProfileId)
            return false;
        var active = await db.Database.SqlQuery<bool?>($"""
            SELECT is_active AS "Value" FROM andril_hobbies.map_attraction_lifecycle
            WHERE attraction_id = {scope.AttractionId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        return active is true && actor.IsAuthenticated && actor.UserId == scope.SubjectId;
    }

    // Called only by a trusted, scope-authorized cleanup coordinator. Keep the
    // claim on uncertain storage outcomes so the same job can retry idempotently.
    internal async Task<bool> TryClaimCleanupAsync(
        MapMediaScope scope, Guid mediaId, Guid claim, CancellationToken cancellationToken)
    {
        if (claim == Guid.Empty)
            return false;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var cutoff = clock.GetUtcNow().ToUniversalTime().AddHours(-24);
        return await Scoped(db, scope)
            .Where(x => x.MediaId == mediaId && x.FinalizedAt == null && x.CleanedAt == null
                && (x.CleanupClaim == null || x.CleanupClaim == claim) && x.ReservedAt <= cutoff
                && !db.MapContributions.Any(y => y.CaptureSessionId == x.SessionId))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.CleanupClaim, claim), cancellationToken) == 1;
    }

    internal async Task<bool> MarkCleanedAsync(MapMediaScope scope, Guid mediaId, Guid claim, CancellationToken cancellationToken)
    {
        if (claim == Guid.Empty)
            return false;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        var rows = Scoped(db, scope)
            .Where(x => x.MediaId == mediaId && x.CleanupClaim == claim && x.FinalizedAt == null
                && !db.MapContributions.Any(y => y.CaptureSessionId == x.SessionId));
        return await rows.Where(x => x.CleanedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.CleanedAt, now), cancellationToken) == 1
            || await rows.AnyAsync(x => x.CleanedAt != null, cancellationToken);
    }

    internal async Task<MapMediaReservation?> FindClaimedOrphanAsync(
        MapMediaScope scope, Guid mediaId, Guid claim, CancellationToken cancellationToken)
    {
        if (claim == Guid.Empty)
            return null;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await Scoped(db, scope).AsNoTracking().SingleOrDefaultAsync(
            x => x.MediaId == mediaId && x.CleanupClaim == claim && x.CleanedAt == null
                && x.FinalizedAt == null && !db.MapContributions.Any(y => y.CaptureSessionId == x.SessionId),
            cancellationToken);
        return row?.Reservation();
    }

    private static IQueryable<MapPrivateMediaRow> Scoped(HobbiesMapDbContext db, MapMediaScope scope) =>
        db.Set<MapPrivateMediaRow>().Where(x => x.SubjectId == scope.SubjectId && x.ProfileId == scope.ProfileId
            && x.AttractionId == scope.AttractionId && x.SessionId == scope.SessionId);
}

// The cleanup caller needs its own approved authorization, independent of a
// contributor's active-account state. No worker or deletion route is registered.
internal interface IMapMediaCleanupAuthorization
{
    Task<bool> AllowsAsync(MapMediaScope scope, CancellationToken cancellationToken);
}

internal sealed class MapPrivateMediaCleanup(MapPrivateMediaStore store,
    IMapPrivateMediaObjectWriter storage, IMapMediaCleanupAuthorization authorization)
{
    internal async Task<bool> RunAsync(MapMediaScope scope, Guid mediaId, Guid claim, CancellationToken cancellationToken)
    {
        if (!await authorization.AllowsAsync(scope, cancellationToken)
            || !await store.TryClaimCleanupAsync(scope, mediaId, claim, cancellationToken))
            return false;
        var orphan = await store.FindClaimedOrphanAsync(scope, mediaId, claim, cancellationToken);
        return orphan is not null && await authorization.AllowsAsync(scope, cancellationToken)
            && await storage.DeleteOrphanAsync(orphan, cancellationToken)
            && await store.MarkCleanedAsync(scope, mediaId, claim, cancellationToken);
    }
}
