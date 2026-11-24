using System.Text.Json;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed partial class MapContributionSubmissionServiceTests
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

    [Theory]
    [InlineData("owner")]
    [InlineData("attraction")]
    [InlineData("session")]
    [InlineData("photo")]
    [InlineData("photo-case")]
    [InlineData("photo-space")]
    [InlineData("description")]
    [InlineData("description-case")]
    [InlineData("description-space")]
    [InlineData("user-latitude")]
    [InlineData("user-longitude")]
    [InlineData("attraction-latitude")]
    [InlineData("attraction-longitude")]
    [InlineData("accuracy")]
    [InlineData("capture-start")]
    [InlineData("capture-end")]
    public async Task OriginalEvidenceRejectsEveryContradictoryRequestField(string changed)
    {
        var f = new Fixture();
        var attempt = await f.Attempt();
        var original = Assert.Single(f.Requests).Contribution;
        var location = changed switch
        {
            "user-latitude" => original.LocationEvidence with { UserLatitude = 1 },
            "user-longitude" => original.LocationEvidence with { UserLongitude = 1 },
            "attraction-latitude" => original.LocationEvidence with { AttractionLatitude = 1 },
            "attraction-longitude" => original.LocationEvidence with { AttractionLongitude = 1 },
            "accuracy" => original.LocationEvidence with { HorizontalAccuracyMetres = 6 },
            "capture-start" => original.LocationEvidence with { CaptureStartedAt = original.LocationEvidence.CaptureStartedAt!.Value.AddTicks(1) },
            "capture-end" => original.LocationEvidence with { CaptureCompletedAt = original.LocationEvidence.CaptureCompletedAt!.Value.AddTicks(1) },
            _ => original.LocationEvidence
        };
        var contradictory = MapContribution.Create(original.ContributionId,
            changed == "owner" ? Guid.NewGuid() : original.OwnerHobbiesModuleProfileId,
            changed == "attraction" ? Guid.NewGuid() : original.AttractionId,
            changed == "session" ? Guid.NewGuid() : original.CaptureSessionId,
            changed switch { "photo" => "private/other", "photo-case" => original.PrivatePhotoStorageObjectKey.ToUpperInvariant(),
                "photo-space" => original.PrivatePhotoStorageObjectKey + " ", _ => original.PrivatePhotoStorageObjectKey },
            changed switch { "description" => "Different", "description-case" => original.Description.ToLowerInvariant(),
                "description-space" => original.Description + " ", _ => original.Description }, location, original.SubmittedAtUtc);

        var retained = Assert.IsType<MapSubmissionOriginalEvidence>(attempt.OriginalEvidence);
        Assert.False(retained.MatchesRequest(contradictory));
        Assert.True(retained.MatchesRequest(original));
        Assert.False(retained.MatchesRequest(null));
        Assert.Single(f.Requests); // Comparing never invokes the writer or preflight again.
    }

    [Theory]
    [InlineData("id")]
    [InlineData("submission-time")]
    [InlineData("capture-offset")]
    [InlineData("new-evidence-instance")]
    public async Task OriginalEvidenceUsesValueEqualityAndExcludesOnlyApprovedGeneratedFields(string equivalent)
    {
        var f = new Fixture();
        var attempt = await f.Attempt();
        var original = Assert.Single(f.Requests).Contribution;
        var location = original.LocationEvidence with { };
        if (equivalent == "capture-offset")
            location = location with
            {
                CaptureStartedAt = location.CaptureStartedAt!.Value.ToOffset(TimeSpan.FromHours(5.5)),
                CaptureCompletedAt = location.CaptureCompletedAt!.Value.ToOffset(TimeSpan.FromHours(-3))
            };
        var sameRequest = MapContribution.Create(equivalent == "id" ? Guid.NewGuid() : original.ContributionId,
            original.OwnerHobbiesModuleProfileId, original.AttractionId, original.CaptureSessionId,
            original.PrivatePhotoStorageObjectKey, original.Description, location,
            equivalent == "submission-time" ? original.SubmittedAtUtc.AddDays(1) : original.SubmittedAtUtc);
        Assert.True(attempt.OriginalEvidence!.MatchesRequest(sameRequest));
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData((int)MapSubmissionOutcome.Created)]
    [InlineData((int)MapSubmissionOutcome.ExistingAuthorized)]
    [InlineData((int)MapSubmissionOutcome.Denied)]
    [InlineData((int)MapSubmissionOutcome.InfrastructureFailure)]
    [InlineData((int)MapSubmissionOutcome.OutcomeUnknown)]
    public async Task InternalAttemptPreservesWriterOutcomesAndOriginalEvidenceWithoutRetry(int value)
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        f.Write = (_, _) => Task.FromResult(new MapSubmissionResult((MapSubmissionOutcome)value, id));
        var attempt = await f.Attempt();
        Assert.Equal((MapSubmissionOutcome)value, attempt.Result.Outcome);
        Assert.Equal(value is (int)MapSubmissionOutcome.Created or (int)MapSubmissionOutcome.ExistingAuthorized ? id : (Guid?)null,
            attempt.Result.ContributionId);
        Assert.True(attempt.OriginalEvidence!.MatchesRequest(Assert.Single(f.Requests).Contribution));
        Assert.Equal(1, f.Calls.Count(call => call == "access"));
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("photo")]
    [InlineData("missing-photo")]
    public async Task DeniedPreparationCannotProduceOriginalEvidence(string denial)
    {
        var f = new Fixture { Denial = denial };
        var attempt = await f.Attempt();
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), attempt.Result);
        Assert.Null(attempt.OriginalEvidence);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task FailedPreparationCannotProduceOriginalEvidence()
    {
        var f = new Fixture { FailAt = "photo" };
        var attempt = await f.Attempt();
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), attempt.Result);
        Assert.Null(attempt.OriginalEvidence);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("photo")]
    [InlineData("writer")]
    public async Task InternalAttemptCancellationPropagatesWithoutReturningEvidence(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var f = new Fixture { CancelAt = stage, Cancellation = cancellation };
        if (stage == "before") cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Attempt(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(stage == "writer" ? 1 : 0, f.Requests.Count);
    }

    [Fact]
    public async Task UnknownWriterFailureRetainsOriginalAndLaterAttemptCannotOverwriteIt()
    {
        var f = new Fixture { Write = (_, _) => throw new IOException("sensitive " + Fixture.PhotoKey) };
        var first = await f.Attempt();
        var firstRequest = Assert.Single(f.Requests).Contribution;
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), first.Result);
        var second = await f.Attempt(description: "Changed retry");
        Assert.True(first.OriginalEvidence!.MatchesRequest(firstRequest));
        Assert.False(first.OriginalEvidence.MatchesRequest(f.Requests[1].Contribution));
        Assert.True(second.OriginalEvidence!.MatchesRequest(f.Requests[1].Contribution));
        Assert.Equal(2, f.Requests.Count); // Two explicit calls, no automatic retries.
    }

    [Fact]
    public async Task CommittedOutcomeAndEvidenceSurviveCancellationAfterWriterCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var f = new Fixture();
        f.Write = (prepared, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.Created, prepared.Contribution.ContributionId));
        };
        var attempt = await f.Attempt(cancellation.Token);
        Assert.Equal(MapSubmissionOutcome.Created, attempt.Result.Outcome);
        Assert.True(attempt.OriginalEvidence!.MatchesRequest(Assert.Single(f.Requests).Contribution));
    }

    [Fact]
    public async Task RetainedEvidenceIsGetterOnlyAndDoesNotSerializeOrPrintPrivateMetadata()
    {
        var f = new Fixture();
        var attempt = await f.Attempt();
        Assert.Equal("{}", JsonSerializer.Serialize(attempt));
        Assert.Equal("{}", JsonSerializer.Serialize(attempt.OriginalEvidence));
        Assert.Equal("Submission attempt (internal).", attempt.ToString());
        Assert.Equal("Original submission evidence (internal).", attempt.OriginalEvidence!.ToString());
        Assert.Empty(typeof(MapSubmissionOriginalEvidence).GetConstructors());
        foreach (var type in new[] { typeof(MapSubmissionAttempt), typeof(MapSubmissionOriginalEvidence) })
        {
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetProperties());
            Assert.All(type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                property => Assert.Null(property.SetMethod));
        }
        Assert.Equal(f.Owner, attempt.OriginalEvidence.OwnerHobbiesModuleProfileId);
        Assert.Equal(f.Attraction.AttractionId, attempt.OriginalEvidence.AttractionId);
        Assert.Equal(f.Session.SessionId, attempt.OriginalEvidence.CaptureSessionId);
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
        internal Task<MapSubmissionAttempt> Attempt(CancellationToken token = default, string description = "Description") =>
            new MapContributionSubmissionService(new(this, this, this, this), this).SubmitAttemptAsync(
                Attraction.AttractionId, Session.SessionId, new(0, 0, 80, 80, 5, null, null),
                Denial == "missing-photo" ? null : PhotoKey, description, token);
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
