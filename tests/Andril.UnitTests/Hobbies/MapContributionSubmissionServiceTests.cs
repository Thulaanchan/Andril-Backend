using System.Text.Json;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionSubmissionServiceTests
{
    [Fact]
    public async Task ValidSubmissionPassesTrustedPreparedPendingContributionAndTokenOnce()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var result = await f.Submit(cancellation.Token);
        Assert.Equal(MapSubmissionOutcome.Created, result.Outcome);
        var saved = Assert.Single(f.Requests).Contribution;
        Assert.Equal(f.Owner, saved.OwnerHobbiesModuleProfileId);
        Assert.Equal(f.Attraction.AttractionId, saved.AttractionId);
        Assert.Equal(f.Session.SessionId, saved.CaptureSessionId);
        Assert.Equal(MapContributionModerationStatus.PendingReview, saved.ModerationStatus);
        Assert.Equal(0, saved.LocationEvidence.AttractionLatitude);
        Assert.Equal(f.Session.StartedAtUtc, saved.LocationEvidence.CaptureStartedAt);
        Assert.Equal(saved.ContributionId, result.ContributionId);
        Assert.Equal(new[] { "access", "session", "attraction", "photo", "writer" }, f.Calls);
        Assert.All(f.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("owner")]
    [InlineData("session")]
    [InlineData("attraction")]
    [InlineData("photo")]
    [InlineData("unknown-photo")]
    [InlineData("missing-photo")]
    [InlineData("expired")]
    public async Task DeniedPreflightNeverInvokesWriterOrExposesPrivateMetadata(string denial)
    {
        var f = new Fixture { Denial = denial };
        var result = await f.Submit();
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), result);
        Assert.Empty(f.Requests);
        Assert.DoesNotContain("writer", f.Calls);
        Assert.DoesNotContain(Fixture.PhotoKey, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("access")]
    [InlineData("session")]
    [InlineData("attraction")]
    [InlineData("photo")]
    public async Task UnavailablePreflightStopsBeforeWriterWithSafeInfrastructureOutcome(string stage)
    {
        var f = new Fixture { FailAt = stage };
        var result = await f.Submit();
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), result);
        Assert.Empty(f.Requests);
        Assert.DoesNotContain(Fixture.PhotoKey, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("access")]
    [InlineData("session")]
    [InlineData("attraction")]
    [InlineData("photo")]
    [InlineData("writer")]
    public async Task CancellationPropagatesWithoutAnotherWrite(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var f = new Fixture { CancelAt = stage, Cancellation = cancellation };
        if (stage == "before") cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Submit(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(stage == "writer" ? 1 : 0, f.Requests.Count);
        if (stage == "before") Assert.Empty(f.Calls);
    }

    [Theory]
    [InlineData((int)MapSubmissionOutcome.Created)]
    [InlineData((int)MapSubmissionOutcome.ExistingAuthorized)]
    [InlineData((int)MapSubmissionOutcome.Denied)]
    [InlineData((int)MapSubmissionOutcome.InfrastructureFailure)]
    [InlineData((int)MapSubmissionOutcome.OutcomeUnknown)]
    public async Task WriterOutcomesArePreservedWithoutAutomaticRetry(int outcomeValue)
    {
        var outcome = (MapSubmissionOutcome)outcomeValue;
        var f = new Fixture();
        var id = Guid.NewGuid();
        f.Write = (_, _) => Task.FromResult(new MapSubmissionResult(outcome, id));
        var result = await f.Submit();
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome is MapSubmissionOutcome.Created or MapSubmissionOutcome.ExistingAuthorized ? id : (Guid?)null,
            result.ContributionId);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task UnexpectedWriterFailureReturnsUnknownWithoutRetryOrExceptionDetails()
    {
        var f = new Fixture { Write = (_, _) => throw new InvalidOperationException("sensitive " + Fixture.PhotoKey) };
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), await f.Submit());
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task WriterCommitOutcomeSurvivesLateCancellation()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Write = (prepared, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.Created, prepared.Contribution.ContributionId));
        };
        Assert.Equal(MapSubmissionOutcome.Created, (await f.Submit(cancellation.Token)).Outcome);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task ExplicitRetryRechecksAuthorizationAndPreservesWriterScopedReconciliation()
    {
        var f = new Fixture();
        Guid? committedId = null;
        f.Write = (prepared, _) =>
        {
            if (committedId.HasValue)
                return Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committedId));
            committedId = prepared.Contribution.ContributionId;
            return Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown));
        };
        Assert.Equal(MapSubmissionOutcome.OutcomeUnknown, (await f.Submit()).Outcome);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committedId), await f.Submit());
        Assert.Equal(2, f.Requests.Count);
        Assert.NotEqual(f.Requests[0].Contribution.ContributionId, f.Requests[1].Contribution.ContributionId);
        f.Denial = "authorization";
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await f.Submit());
        Assert.Equal(2, f.Requests.Count);
        Assert.Equal(3, f.Calls.Count(call => call == "access"));
    }

    private sealed class Fixture : IMapCaptureSessionAccessReader, IMapContributionCaptureSessionRepository,
        IMapAttractionRepository, IMapContributionPhotoEvidenceReader, IMapContributionSubmissionWriter
    {
        internal const string PhotoKey = "private/sensitive-camera-key";
        internal Guid Owner { get; } = Guid.NewGuid();
        internal MapAttraction Attraction { get; } = MapAttraction.Create(Guid.NewGuid(), "Canonical", 0, 0);
        internal MapContributionCaptureSession Session { get; }
        internal string? Denial { get; set; }
        internal string? FailAt { get; init; }
        internal string? CancelAt { get; init; }
        internal CancellationTokenSource? Cancellation { get; init; }
        internal List<string> Calls { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal List<MapContributionSubmissionPreflight.PreparedSubmission> Requests { get; } = [];
        internal Func<MapContributionSubmissionPreflight.PreparedSubmission, CancellationToken, Task<MapSubmissionResult>> Write { get; set; }
            = (p, _) => Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.Created, p.Contribution.ContributionId));
        internal Fixture()
        {
            Session = MapContributionCaptureSession.Create(Guid.NewGuid(), Owner, Attraction.AttractionId, DateTimeOffset.UtcNow.AddSeconds(-10));
            Session.Complete(Session.StartedAtUtc.AddSeconds(5));
        }
        internal Task<MapSubmissionResult> Submit(CancellationToken token = default) =>
            new MapContributionSubmissionService(new(this, this, this, this), this).SubmitAsync(
                Attraction.AttractionId, Session.SessionId, new(0, 0, 80, 80, 5, null, null),
                Denial == "missing-photo" ? null : PhotoKey, "Description", token);
        private Task<T> Respond<T>(string stage, T value, CancellationToken token)
        {
            Calls.Add(stage); Tokens.Add(token);
            if (FailAt == stage) throw new InvalidOperationException("sensitive " + PhotoKey);
            if (CancelAt == stage) Cancellation!.Cancel();
            return Task.FromResult(value);
        }
        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken) =>
            Respond<Guid?>("access", Denial == "authorization" ? null : Owner, cancellationToken);
        public Task<MapContributionCaptureSession?> FindOwnedAsync(Guid sessionId, Guid ownerHobbiesModuleProfileId,
            Guid attractionId, CancellationToken cancellationToken)
        {
            Assert.Equal((Session.SessionId, Owner, Attraction.AttractionId), (sessionId, ownerHobbiesModuleProfileId, attractionId));
            var found = Session;
            if (Denial is "owner" or "session" or "attraction" or "expired")
            {
                found = MapContributionCaptureSession.Create(Denial == "session" ? Guid.NewGuid() : sessionId,
                    Denial == "owner" ? Guid.NewGuid() : Owner, Denial == "attraction" ? Guid.NewGuid() : attractionId,
                    Denial == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : Session.StartedAtUtc);
                found.Complete(found.StartedAtUtc.AddSeconds(5));
            }
            return Respond<MapContributionCaptureSession?>("session", found, cancellationToken);
        }
        public Task<MapAttraction?> FindByIdAsync(Guid attractionId, CancellationToken cancellationToken) =>
            Respond<MapAttraction?>("attraction", Attraction, cancellationToken);
        public Task<MapContributionPhotoVerificationResult> VerifyAsync(Guid ownerHobbiesModuleProfileId, Guid attractionId,
            Guid captureSessionId, string? privatePhotoStorageObjectKey, CancellationToken cancellationToken)
        {
            Assert.Equal((Owner, Attraction.AttractionId, Session.SessionId, PhotoKey),
                (ownerHobbiesModuleProfileId, attractionId, captureSessionId, privatePhotoStorageObjectKey));
            return Respond("photo", Denial switch { "photo" => MapContributionPhotoVerificationResult.Denied,
                "unknown-photo" => (MapContributionPhotoVerificationResult)99, _ => MapContributionPhotoVerificationResult.Verified }, cancellationToken);
        }
        public Task<MapSubmissionResult> SubmitAsync(MapContributionSubmissionPreflight.PreparedSubmission prepared, CancellationToken cancellationToken)
        {
            Requests.Add(prepared);
            Respond("writer", true, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            return Write(prepared, cancellationToken);
        }
        public Task AddAsync(MapContributionCaptureSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryCompleteAsync(Guid sessionId, Guid ownerHobbiesModuleProfileId, Guid attractionId,
            DateTimeOffset completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddAsync(MapAttraction attraction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MapAttractionDiscoveryItem>> DiscoverAsync(MapAttractionDiscoveryQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
