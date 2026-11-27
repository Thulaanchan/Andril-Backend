using System.Data;
using System.Runtime.CompilerServices;
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

[assembly: InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.Persistence.Repositories.Hobbies;

// Identifies the durable approval for eligibility evaluation only.
// A source does not establish a reward entitlement or a processed credit.
internal readonly record struct MapContributionRewardSource(
    Guid ContributionId, Guid HobbiesProfileId, Guid AttractionId, Guid DecisionId);

// Unregistered. The authorization adapter must establish the active Hobbies owner
// and hold its authorization fence through the evidence read.
internal sealed class MapContributionRewardEligibilityReader(
    IDbContextFactory<HobbiesMapDbContext> contexts,
    IMapSubmissionTransactionAuthorization? authorization = null)
{
    internal async Task<MapContributionRewardSource?> FindEligibleSourceAsync(
        MapModerationScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authorization is null || scope.ContributionId == Guid.Empty || scope.AttractionId == Guid.Empty)
            return null;

        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Reward eligibility does not accept ambient transactions.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (db.Database.GetDbConnection() is not NpgsqlConnection { State: ConnectionState.Closed } connection
            || db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            return null;

        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        if (settings.Enlist || settings.Multiplexing || settings.NoResetOnClose)
            return null;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var owner = await authorization.ResolveActiveHobbiesOwnerAsync(
            connection, transaction.GetDbTransaction(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (owner is not Guid profile || profile == Guid.Empty)
            return null;

        var evidence = await QueryEvidence(db, scope, profile).SingleOrDefaultAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return evidence is null ? null : VerifyEvidence(
            scope, profile, evidence.Contribution, evidence.Receipt, evidence.Audit, evidence.Media);
    }

    internal sealed record Evidence(
        MapContribution Contribution, MapModerationReceipt Receipt, MapModerationAudit Audit, MapPrivateMediaRow Media);

    internal static IQueryable<Evidence> QueryEvidence(HobbiesMapDbContext db, MapModerationScope scope, Guid profile) =>
        from contribution in db.MapContributions.AsNoTracking()
            join receipt in db.Set<MapModerationReceipt>().AsNoTracking()
                on contribution.ContributionId equals receipt.ContributionId
            join audit in db.Set<MapModerationAudit>().AsNoTracking()
                on receipt.DecisionId equals audit.DecisionId
            join media in db.Set<MapPrivateMediaRow>().AsNoTracking()
                on receipt.MediaId equals (Guid?)media.MediaId
            where contribution.ContributionId == scope.ContributionId
                && contribution.AttractionId == scope.AttractionId
                && contribution.OwnerHobbiesModuleProfileId == profile
            select new Evidence(contribution, receipt, audit, media);

    internal static MapContributionRewardSource? VerifyEvidence(
        MapModerationScope scope, Guid owner, MapContribution contribution,
        MapModerationReceipt? receipt, MapModerationAudit? audit, MapPrivateMediaRow? media)
    {
        if (owner == Guid.Empty || contribution.ContributionId != scope.ContributionId
            || contribution.AttractionId != scope.AttractionId || contribution.OwnerHobbiesModuleProfileId != owner
            || receipt is null || receipt.Action != MapContributionReviewAction.Approve
            || receipt.ContributionId != scope.ContributionId || receipt.AttractionId != scope.AttractionId
            || receipt.ProfileId != owner || receipt.SessionId != contribution.CaptureSessionId
            || receipt.ExpectedRevision != 0 || receipt.Revision != 1
            || receipt.DecisionId == Guid.Empty || receipt.ReviewerSubject == Guid.Empty
            || receipt.DecidedAt == default || audit?.DecisionId != receipt.DecisionId)
            return null;

        if (media is null || media.MediaId == Guid.Empty || receipt.MediaId != media.MediaId
            || media.AttractionId != scope.AttractionId || media.ProfileId != owner
            || media.SessionId != contribution.CaptureSessionId || media.CleanupClaim is not null
            || media.CleanedAt is not null || media.FinalizedAt is not { } finalizedAt
            || media.CapturedAt is not { } capturedAt || capturedAt > finalizedAt
            || !string.Equals(media.PrivateReference, contribution.PrivatePhotoStorageObjectKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(media.Version) || media.Sha256 is not { Length: 64 } digest
            || !digest.All(Uri.IsHexDigit))
            return null;

        return new(scope.ContributionId, owner, scope.AttractionId, receipt.DecisionId);
    }
}
