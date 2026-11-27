using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapAttractionLifecycleMigrationAcceptanceTests
{
    [MapCapturePostgresFact]
    public Task RewardEligibility_RequiresDurableApprovalAndActiveHobbiesScope() =>
        WithDisposableDatabaseAsync(async connectionString =>
        {
            await using var db = CreateContext(connectionString);
            await db.Database.MigrateAsync(Moderation);
            var contexts = new ContextFactory(connectionString);
            var clock = new MediaClock();
            var profile = Guid.NewGuid();
            var attraction = MapAttraction.Create(Guid.NewGuid(), "Reward evidence", 0, 0);
            db.Add(attraction);

            MapContribution SeedContribution(string key)
            {
                var session = MapContributionCaptureSession.Create(
                    Guid.NewGuid(), profile, attraction.AttractionId, clock.Now.AddSeconds(-20));
                session.Complete(clock.Now.AddSeconds(-10));
                var contribution = MapContribution.Create(
                    Guid.NewGuid(), profile, attraction.AttractionId, session.SessionId, key, "Test photo",
                    new MapContributionLocationEvidence(null, null, null, null, null, null, null), clock.Now);
                db.AddRange(session, contribution);
                return contribution;
            }

            var approved = SeedContribution("private/reward-approved.jpg");
            var rejected = SeedContribution("private/reward-rejected.jpg");
            var media = new VerifiedMapMedia(Guid.NewGuid(), approved.PrivatePhotoStorageObjectKey, "v1", new string('A', 64));
            db.Add(new MapPrivateMediaRow
            {
                MediaId = media.MediaId, SubjectId = Guid.NewGuid(), ProfileId = profile,
                AttractionId = attraction.AttractionId, SessionId = approved.CaptureSessionId, Challenge = Guid.NewGuid(),
                ReservedAt = clock.Now.AddSeconds(-18), UploadExpiresAt = clock.Now.AddSeconds(100),
                PrivateReference = media.PrivateReference, Version = media.Version, Sha256 = media.Sha256,
                CapturedAt = clock.Now.AddSeconds(-15), FinalizedAt = clock.Now
            });
            await db.SaveChangesAsync();

            var reader = new MapContributionRewardEligibilityReader(contexts, new MediaAuthorization(profile));
            var scope = new MapModerationScope(attraction.AttractionId, approved.ContributionId);
            var rejectedScope = scope with { ContributionId = rejected.ContributionId };
            Assert.Null(await reader.FindEligibleSourceAsync(scope));

            var reviewer = new TestUser(Guid.NewGuid());
            var moderation = new MapModerationRepository(
                contexts, reviewer, clock, new ModerationAuthorization { Allowed = true });
            var approval = new MapContributionReviewRequest(
                Guid.NewGuid(), approved.ContributionId, MapContributionReviewAction.Approve, 0,
                reviewer.UserId!.Value.ToString("D"));
            var rejection = new MapContributionReviewRequest(
                Guid.NewGuid(), rejected.ContributionId, MapContributionReviewAction.Reject, 0,
                reviewer.UserId.Value.ToString("D"));
            Assert.Equal(MapModerationOutcome.Applied, await moderation.DecideAsync(scope, approval, media, default));
            Assert.Equal(MapModerationOutcome.Applied, await moderation.DecideAsync(rejectedScope, rejection, null, default));

            var source = new MapContributionRewardSource(
                approved.ContributionId, profile, attraction.AttractionId, approval.DecisionId);
            Assert.Equal(source, await reader.FindEligibleSourceAsync(scope));
            var freshReader = new MapContributionRewardEligibilityReader(
                new ContextFactory(connectionString), new MediaAuthorization(profile));
            Assert.Equal(source, await freshReader.FindEligibleSourceAsync(scope));
            Assert.Null(await reader.FindEligibleSourceAsync(rejectedScope));
            Assert.Null(await reader.FindEligibleSourceAsync(scope with { AttractionId = Guid.NewGuid() }));
            Assert.Null(await new MapContributionRewardEligibilityReader(contexts, new MediaAuthorization(Guid.NewGuid()))
                .FindEligibleSourceAsync(scope));
            // The fixture adapters supply synthetic authorization. No real module grant is exercised.
            Assert.Null(await new MapContributionRewardEligibilityReader(contexts, new MediaAuthorization(Guid.Empty))
                .FindEligibleSourceAsync(scope));
            Assert.Empty(await db.FlightPointsLedgerEntries.ToListAsync());
        });
}
