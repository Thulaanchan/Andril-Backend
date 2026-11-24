using Andril.Economy.FlightPoints;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Economy;

// Every candidate-specific assertion in FP-03 is PROPOSED TEST-ONLY behavior.
// Component identity is not business entitlement uniqueness or production authorization.
public sealed class FlightPointsRewardEventIdentityPrototypeTests
{
    [Fact]
    public void Candidate_StableValueEqualityDoesNotDependOnObjectOrPolicyVersion()
    {
        var source = new ProposedRewardSource();
        var first = source.Intent("synthetic-policy-A");
        var retry = source.Intent("synthetic-policy-A");
        var changedPolicy = source.Intent("synthetic-policy-B");
        Assert.NotSame(first.Identity, retry.Identity);
        Assert.Equal(first.Identity, retry.Identity);
        Assert.Equal(first.Identity.GetHashCode(), retry.Identity.GetHashCode());
        Assert.Equal(first.Identity, changedPolicy.Identity);
        Assert.NotEqual(first, changedPolicy); // Same identity does not mean equivalent receipt evidence.
        Assert.Single(new HashSet<ProposedRewardEventIdentity> { first.Identity, retry.Identity, changedPolicy.Identity });
    }

    [Theory]
    [InlineData("contribution")]
    [InlineData("decision")]
    [InlineData("component")]
    public void Candidate_DistinctSourceOrComponentChangesIdentity(string changed)
    {
        var source = new ProposedRewardSource(); var original = source.Key();
        var other = new ProposedRewardEventIdentity("Hobbies",
            changed == "contribution" ? Guid.NewGuid() : original.ContributionId,
            changed == "decision" ? Guid.NewGuid() : original.DecisionId,
            changed == "component" ? FlightPointsRewardType.FirstExplorer : original.RewardType);
        Assert.NotEqual(original, other);
        Assert.Equal(2, new HashSet<ProposedRewardEventIdentity> { original, other }.Count);
        // Different events may still compete for one business entitlement. That scope is PENDING DECISION.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hobbies")]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("Hobbies ")]
    public void Candidate_OnlyExactSyntheticHobbiesModuleIsAccepted(string? module)
    {
        Assert.Throws<ArgumentException>(() => new ProposedRewardEventIdentity(module!, Guid.NewGuid(),
            Guid.NewGuid(), FlightPointsRewardType.FirstEligibleContribution));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Candidate_DefaultSourceIdentifiersAreRejected(bool contribution)
    {
        Assert.Throws<ArgumentException>(() => new ProposedRewardEventIdentity("Hobbies",
            contribution ? Guid.Empty : Guid.NewGuid(), contribution ? Guid.NewGuid() : Guid.Empty,
            FlightPointsRewardType.FirstEligibleContribution));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void Candidate_UndefinedRewardComponentIsRejected(int component) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProposedRewardEventIdentity("Hobbies",
            Guid.NewGuid(), Guid.NewGuid(), (FlightPointsRewardType)component));

    [Fact]
    public void Candidate_GrantComponentsUseExistingPolicyAndRetainSeparateIdentity()
    {
        var source = new ProposedRewardSource();
        var first = source.Intent(); var explorer = source.Intent(component: FlightPointsRewardType.FirstExplorer);
        Assert.NotEqual(first.Identity, explorer.Identity);
        Assert.Equal(FlightPointsRewardPolicy.FirstEligibleContributionPoints, first.Points);
        Assert.Equal(FlightPointsRewardPolicy.FirstExplorerBonusPoints, explorer.Points);
        Assert.Equal(first.Owner, explorer.Owner); Assert.Equal(first.AcceptedAtUtc, explorer.AcceptedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Candidate_MissingPolicyEvidenceCannotConstructIntent(string? policy) =>
        Assert.ThrowsAny<ArgumentException>(() => new ProposedRewardSource().Intent(policy!));

    [Fact]
    public void Candidate_ValuesExposeNoMutableIdentityOrReceiptEvidence()
    {
        foreach (var type in new[] { typeof(ProposedRewardEventIdentity), typeof(ProposedRewardIntent) })
        {
            Assert.True(type.IsSealed);
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
    }
}

internal sealed record ProposedRewardEventIdentity
{
    internal ProposedRewardEventIdentity(string module, Guid contributionId, Guid decisionId, FlightPointsRewardType rewardType)
    {
        if (module != "Hobbies") throw new ArgumentException("Proposed synthetic Hobbies module required.", nameof(module));
        if (contributionId == Guid.Empty) throw new ArgumentException("Contribution required.", nameof(contributionId));
        if (decisionId == Guid.Empty) throw new ArgumentException("Accepted decision required.", nameof(decisionId));
        if (rewardType is not (FlightPointsRewardType.FirstEligibleContribution or FlightPointsRewardType.FirstExplorer))
            throw new ArgumentOutOfRangeException(nameof(rewardType));
        Module = module; ContributionId = contributionId; DecisionId = decisionId; RewardType = rewardType;
    }
    public string Module { get; }
    public Guid ContributionId { get; }
    public Guid DecisionId { get; }
    public FlightPointsRewardType RewardType { get; }
}

internal sealed record ProposedRewardIntent
{
    private ProposedRewardIntent(ProposedRewardEventIdentity identity, MapContributionModerationSnapshot source,
        string policy, int points)
    {
        Identity = identity; Owner = source.OwnerHobbiesModuleProfileId; Attraction = source.AttractionId;
        Session = source.CaptureSessionId; Revision = source.Revision; Reviewer = source.Decision!.Request.ReviewerSubject;
        Policy = policy; Points = points; AcceptedAtUtc = source.Decision.DecidedAtUtc;
    }
    public ProposedRewardEventIdentity Identity { get; }
    public Guid Owner { get; }
    public Guid Attraction { get; }
    public Guid Session { get; }
    public long Revision { get; }
    public string Reviewer { get; }
    public string Policy { get; }
    public int Points { get; }
    public DateTimeOffset AcceptedAtUtc { get; }

    internal static ProposedRewardIntent FromSyntheticSource(ProposedRewardEventIdentity identity,
        Guid syntheticTrustedOwner, MapContribution contribution, MapContributionModerationSnapshot source,
        string policy, FlightPointsEligibility syntheticFacts)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(contribution); ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        if (syntheticTrustedOwner == Guid.Empty || syntheticTrustedOwner != contribution.OwnerHobbiesModuleProfileId)
            throw new UnauthorizedAccessException("Synthetic owner scope denied.");
        var action = source.Decision?.Request.Action;
        var restored = MapContributionModeration.Restore(contribution, source, action).Snapshot;
        if (action != MapContributionReviewAction.Approve || identity.ContributionId != restored.ContributionId
            || identity.DecisionId != restored.Decision!.Request.DecisionId)
            throw new InvalidOperationException("Proposed component requires matching accepted approval evidence.");
        var calculation = FlightPointsRewardPolicy.Calculate(syntheticFacts with { IsAdminApproved = true });
        var points = identity.RewardType == FlightPointsRewardType.FirstEligibleContribution
            ? calculation.FirstContributionPoints : calculation.FirstExplorerPoints;
        if (calculation.RequiresEligibilityResolution || points == 0)
            throw new InvalidOperationException("Proposed component eligibility is unresolved or denied.");
        return new(identity, restored, policy, points);
    }
}

internal sealed class ProposedRewardSource
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
    internal MapContribution Contribution { get; } = MapContribution.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "private/synthetic/fp03.jpg", "Synthetic FP03",
        new MapContributionLocationEvidence(7, 80, 7, 80, 5, Now.AddSeconds(-30), Now.AddSeconds(-5)), Now);
    internal MapContributionModerationSnapshot Accepted { get; }
    internal ProposedRewardSource()
    {
        var moderation = MapContributionModeration.CreatePending(Contribution, 1);
        Accepted = moderation.Decide(new(Guid.NewGuid(), Contribution.ContributionId,
            MapContributionReviewAction.Approve, 1, "synthetic-reviewer"), Now);
    }
    internal ProposedRewardEventIdentity Key(FlightPointsRewardType component = FlightPointsRewardType.FirstEligibleContribution) =>
        new("Hobbies", Contribution.ContributionId, Accepted.Decision!.Request.DecisionId, component);
    internal ProposedRewardIntent Intent(string policy = "synthetic-policy-A",
        FlightPointsRewardType component = FlightPointsRewardType.FirstEligibleContribution) =>
        ProposedRewardIntent.FromSyntheticSource(Key(component), Contribution.OwnerHobbiesModuleProfileId,
            Contribution, Accepted, policy, new(true, false, true, false, true, true));
}
