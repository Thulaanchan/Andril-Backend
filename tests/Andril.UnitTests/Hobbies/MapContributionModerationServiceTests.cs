using Andril.Hobbies.Map.Contributions;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionModerationServiceTests
{
    [Fact]
    public async Task UnauthorizedReviewerCannotReadOrWrite()
    {
        var fixture = new Fixture { Authorized = false };

        Assert.Null(await fixture.Service.FindPendingAsync(fixture.Item.Scope));
        Assert.Equal(MapModerationOutcome.Denied, await fixture.DecideAsync(MapContributionReviewAction.Approve));
        Assert.Null(await fixture.Service.InspectPendingEvidenceAsync(fixture.Item.Scope));
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public async Task ApprovalRequiresEvidenceAndRechecksAuthorization()
    {
        var fixture = new Fixture();
        Assert.Equal(MapModerationOutcome.Ineligible, await fixture.DecideAsync(MapContributionReviewAction.Approve));
        Assert.Null(fixture.Request);

        fixture.Evidence = new(Guid.NewGuid(), "private", "version", new string('A', 64));
        fixture.RevokeOnInspection = true;
        Assert.Equal(MapModerationOutcome.Denied, await fixture.DecideAsync(MapContributionReviewAction.Approve));
        Assert.Null(fixture.Request);

        fixture.RevokeOnInspection = false;
        fixture.Authorized = true;
        Assert.Equal(MapModerationOutcome.Applied, await fixture.DecideAsync(MapContributionReviewAction.Approve));
        Assert.Equal(fixture.Evidence, fixture.WrittenEvidence);
        Assert.Equal(fixture.UserId?.ToString("D"), fixture.Request?.ReviewerSubject);
    }

    [Fact]
    public async Task RejectionAndTerminalRetryDoNotDependOnStorage()
    {
        var fixture = new Fixture();
        Assert.Equal(MapModerationOutcome.Applied, await fixture.DecideAsync(MapContributionReviewAction.Reject));
        Assert.Null(fixture.WrittenEvidence);
        fixture.Item = fixture.Item with { IsPending = false, Revision = 1 };
        fixture.Outcome = MapModerationOutcome.Replay;
        Assert.Equal(MapModerationOutcome.Replay, await fixture.DecideAsync(MapContributionReviewAction.Reject));
        Assert.Equal(0, fixture.Inspections);
    }

    [Fact]
    public async Task ScopedLookupMismatchIsDenied()
    {
        var fixture = new Fixture { ReturnForeignItem = true };
        Assert.Null(await fixture.Service.FindPendingAsync(fixture.Item.Scope));
        Assert.Equal(MapModerationOutcome.Denied, await fixture.DecideAsync(MapContributionReviewAction.Reject));
        Assert.Null(fixture.Request);
    }

    private sealed class Fixture : ICurrentUser, IMapModerationAuthorization,
        IMapModerationRepository, IMapModerationEvidenceReader
    {
        private readonly Guid decisionId = Guid.NewGuid();
        public bool IsAuthenticated => true;
        public Guid? UserId { get; } = Guid.NewGuid();
        internal bool Authorized { get; set; } = true;
        internal bool RevokeOnInspection { get; set; }
        internal bool ReturnForeignItem { get; set; }
        internal int Reads { get; private set; }
        internal int Inspections { get; private set; }
        internal VerifiedMapMedia? Evidence { get; set; }
        internal VerifiedMapMedia? WrittenEvidence { get; private set; }
        internal MapContributionReviewRequest? Request { get; private set; }
        internal MapModerationOutcome Outcome { get; set; } = MapModerationOutcome.Applied;
        internal MapModerationItem Item { get; set; } = new(
            new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), 0, true);
        internal MapContributionModerationService Service => new(this, this, this, this);

        internal Task<MapModerationOutcome> DecideAsync(MapContributionReviewAction action) =>
            Service.DecideAsync(Item.Scope, decisionId, action, 0);

        public Task<bool> CanReviewAsync(Guid reviewer, MapModerationScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Authorized && reviewer == UserId && scope == Item.Scope);

        public Task<MapModerationItem?> FindAsync(MapModerationScope scope, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<MapModerationItem?>(ReturnForeignItem
                ? Item with { Scope = scope with { AttractionId = Guid.NewGuid() } } : Item);
        }

        public Task<VerifiedMapMedia?> InspectAsync(MapModerationItem item, CancellationToken cancellationToken)
        {
            Inspections++;
            if (RevokeOnInspection)
                Authorized = false;
            return Task.FromResult(Evidence);
        }

        public Task<MapModerationOutcome> DecideAsync(MapModerationScope scope, MapContributionReviewRequest request,
            VerifiedMapMedia? approvalEvidence, CancellationToken cancellationToken)
        {
            Request = request;
            WrittenEvidence = approvalEvidence;
            return Task.FromResult(Outcome);
        }
    }
}
