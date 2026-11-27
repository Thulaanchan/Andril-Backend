using System.Data.Common;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionRewardEligibilityReaderTests
{
    private static readonly DateTimeOffset SubmittedAt = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private readonly Guid _profile = Guid.NewGuid();
    private readonly Guid _session = Guid.NewGuid();
    private readonly Guid _decision = Guid.NewGuid();
    private readonly Guid _media = Guid.NewGuid();
    private readonly MapModerationScope _scope = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void DurableApprovalReturnsTheSameSourceOnExactRepeat()
    {
        var contribution = Contribution();
        var receipt = Receipt();
        var audit = new MapModerationAudit { DecisionId = _decision };
        var media = Media();

        var first = MapContributionRewardEligibilityReader.VerifyEvidence(_scope, _profile, contribution, receipt, audit, media);
        var repeated = MapContributionRewardEligibilityReader.VerifyEvidence(_scope, _profile, contribution, receipt, audit, media);

        Assert.Equal(new MapContributionRewardSource(_scope.ContributionId, _profile, _scope.AttractionId, _decision), first);
        Assert.Equal(first, repeated);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("missing-audit")]
    [InlineData("wrong-audit")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-attraction")]
    [InlineData("wrong-contribution")]
    [InlineData("wrong-receipt-owner")]
    [InlineData("wrong-receipt-attraction")]
    [InlineData("wrong-receipt-session")]
    [InlineData("wrong-revision")]
    [InlineData("wrong-expected-revision")]
    [InlineData("missing-decision-time")]
    [InlineData("missing-media")]
    [InlineData("wrong-media-id")]
    [InlineData("wrong-media-owner")]
    [InlineData("wrong-media-attraction")]
    [InlineData("wrong-media-session")]
    [InlineData("wrong-media-key")]
    [InlineData("missing-media-version")]
    [InlineData("invalid-media-digest")]
    [InlineData("unfinalized-media")]
    [InlineData("missing-capture-time")]
    [InlineData("cleanup-claimed-media")]
    public void MissingOrConflictingEvidenceDeniesEligibility(string defect)
    {
        var owner = defect == "wrong-owner" ? Guid.NewGuid() : _profile;
        var scope = defect switch
        {
            "wrong-attraction" => _scope with { AttractionId = Guid.NewGuid() },
            "wrong-contribution" => _scope with { ContributionId = Guid.NewGuid() },
            _ => _scope
        };
        var audit = defect == "missing-audit" ? null : new MapModerationAudit
        {
            DecisionId = defect == "wrong-audit" ? Guid.NewGuid() : _decision
        };

        Assert.Null(MapContributionRewardEligibilityReader.VerifyEvidence(
            scope, owner, Contribution(), defect == "pending" ? null : Receipt(defect),
            audit, defect == "missing-media" ? null : Media(defect)));
    }

    [Fact]
    public async Task MissingAuthorizationDeniesWithoutOpeningAContext()
    {
        var reader = new MapContributionRewardEligibilityReader(new NeverCreateContext());
        Assert.Null(await reader.FindEligibleSourceAsync(_scope));
    }

    [Fact]
    public async Task EmptyScopeDeniesBeforeAuthorizationOrDatabaseAccess()
    {
        var reader = new MapContributionRewardEligibilityReader(new NeverCreateContext(), new NeverAuthorize());

        Assert.Null(await reader.FindEligibleSourceAsync(_scope with { AttractionId = Guid.Empty }));
        Assert.Null(await reader.FindEligibleSourceAsync(_scope with { ContributionId = Guid.Empty }));
    }

    [Fact]
    public void EvidenceQueryTranslatesWithAllRequiredJoinsAndOwnerScope()
    {
        var options = new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Enlist=false").Options;
        using var context = new HobbiesMapDbContext(options);
        var sql = MapContributionRewardEligibilityReader.QueryEvidence(context, _scope, _profile).ToQueryString();

        Assert.Contains("INNER JOIN andril_hobbies.map_moderation_receipts", sql);
        Assert.Contains("INNER JOIN andril_hobbies.map_moderation_audit", sql);
        Assert.Contains("INNER JOIN andril_hobbies.map_private_media", sql);
        Assert.Contains("owner_hobbies_module_profile_id =", sql);
        Assert.Contains("attraction_id =", sql);
        Assert.Contains("contribution_id =", sql);
    }

    private MapContribution Contribution() => MapContribution.Create(
        _scope.ContributionId, _profile, _scope.AttractionId, _session, "private/photo.jpg", "Photo",
        new MapContributionLocationEvidence(null, null, null, null, null, null, null), SubmittedAt);

    private MapModerationReceipt Receipt(string? defect = null) => new()
    {
        DecisionId = _decision,
        ContributionId = _scope.ContributionId,
        AttractionId = defect == "wrong-receipt-attraction" ? Guid.NewGuid() : _scope.AttractionId,
        ProfileId = defect == "wrong-receipt-owner" ? Guid.NewGuid() : _profile,
        SessionId = defect == "wrong-receipt-session" ? Guid.NewGuid() : _session,
        ReviewerSubject = Guid.NewGuid(),
        Action = defect == "rejected" ? MapContributionReviewAction.Reject : MapContributionReviewAction.Approve,
        ExpectedRevision = defect == "wrong-expected-revision" ? 1 : 0,
        Revision = defect == "wrong-revision" ? 2 : 1,
        DecidedAt = defect == "missing-decision-time" ? default : SubmittedAt.AddMinutes(1),
        MediaId = defect == "rejected" ? null : _media
    };

    private MapPrivateMediaRow Media(string? defect = null) => new()
    {
        MediaId = defect == "wrong-media-id" ? Guid.NewGuid() : _media,
        AttractionId = defect == "wrong-media-attraction" ? Guid.NewGuid() : _scope.AttractionId,
        ProfileId = defect == "wrong-media-owner" ? Guid.NewGuid() : _profile,
        SessionId = defect == "wrong-media-session" ? Guid.NewGuid() : _session,
        PrivateReference = defect == "wrong-media-key" ? "private/other.jpg" : "private/photo.jpg",
        Version = defect == "missing-media-version" ? null : "v1",
        Sha256 = new string(defect == "invalid-media-digest" ? 'Z' : 'A', 64),
        CapturedAt = defect == "missing-capture-time" ? null : SubmittedAt.AddSeconds(-2),
        FinalizedAt = defect == "unfinalized-media" ? null : SubmittedAt.AddSeconds(-1),
        CleanupClaim = defect == "cleanup-claimed-media" ? Guid.NewGuid() : null
    };

    private sealed class NeverCreateContext : IDbContextFactory<HobbiesMapDbContext>
    {
        public HobbiesMapDbContext CreateDbContext() =>
            throw new InvalidOperationException("Authorization must be configured before opening the context.");
    }

    private sealed class NeverAuthorize : IMapSubmissionTransactionAuthorization
    {
        public Task<Guid?> ResolveActiveHobbiesOwnerAsync(DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid scope must be rejected before authorization.");
    }
}
