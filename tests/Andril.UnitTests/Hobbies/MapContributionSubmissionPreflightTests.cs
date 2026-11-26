using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionSubmissionPreflightTests
{
    [Fact]
    public async Task PreparedSubmissionUsesCanonicalEvidenceAndCannotBeForged()
    {
        var ports = new TestPorts();
        var before = ports.Clock.GetUtcNow();
        var prepared = await new MapContributionSubmissionPreflight(ports, ports, ports, ports, ports.Clock)
            .PrepareAsync(AttractionId, SessionId, ports.Observation, PhotoKey, "Description");
        Assert.NotNull(prepared);
        var contribution = prepared.Contribution;
        Assert.NotEqual(Guid.Empty, contribution.ContributionId);
        Assert.Equal(OwnerId, contribution.OwnerHobbiesModuleProfileId);
        Assert.Equal(AttractionId, contribution.AttractionId);
        Assert.Equal(SessionId, contribution.CaptureSessionId);
        Assert.Equal(Start, contribution.LocationEvidence.CaptureStartedAt);
        Assert.Equal(Start.AddSeconds(10), contribution.LocationEvidence.CaptureCompletedAt);
        Assert.Equal(ports.Attraction!.Latitude, contribution.LocationEvidence.AttractionLatitude);
        Assert.Equal(before, contribution.SubmittedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            new MapContributionSubmissionPreflight.PreparedSubmission(contribution, new object()));
    }

    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid AttractionId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ForeignId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private const string PhotoKey = "private-camera-object";
    private static readonly Guid AccountSubject = Guid.NewGuid();

    [Fact]
    public async Task ServerCameraBindingAndCanonicalLocationReachPendingSubmission()
    {
        var ports = new TestPorts();
        var actor = new CameraActor();
        var source = new CameraSource(() => CameraEvidence());
        var reader = new TrustedMapContributionPhotoEvidenceReader(actor, ports, ports, source, ports.Clock);
        var preflight = new MapContributionSubmissionPreflight(ports, ports, ports, reader, ports.Clock);
        var writer = new WriterProbe();
        var service = new MapContributionSubmissionService(preflight, writer);

        var substituted = await service.SubmitAsync(AttractionId, SessionId,
            ports.Observation, "substituted-photo", "Description");
        Assert.Equal(MapSubmissionOutcome.Denied, substituted.Outcome);
        Assert.Equal(0, writer.Calls);

        var attempt = await service.SubmitAttemptAsync(AttractionId, SessionId,
            ports.Observation, PhotoKey, "Description");

        Assert.NotNull(attempt.OriginalEvidence);
        Assert.Equal(MapSubmissionOutcome.Created, attempt.Result.Outcome);
        Assert.Equal((AccountSubject, OwnerId, AttractionId, SessionId), source.Request);
        Assert.Equal(2, source.Calls);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(MapContributionModerationStatus.PendingReview, writer.Contribution!.ModerationStatus);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("attraction")]
    [InlineData("session")]
    [InlineData("object")]
    [InlineData("gallery")]
    [InlineData("unknown origin")]
    [InlineData("before capture")]
    [InlineData("after capture")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public async Task AuthoritativeRecordMustMatchTheWholeCameraScope(string scenario)
    {
        var ports = new TestPorts();
        var evidence = CameraEvidence();
        var source = new CameraSource(() => scenario switch
        {
            "account" => evidence with { AccountSubjectId = ForeignId },
            "profile" => evidence with { OwnerHobbiesModuleProfileId = ForeignId },
            "attraction" => evidence with { AttractionId = ForeignId },
            "session" => evidence with { CaptureSessionId = ForeignId },
            "object" => evidence with { PrivateObjectReference = "other-private-object" },
            "gallery" => evidence with { Origin = MapPhotoOrigin.GalleryUpload },
            "unknown origin" => evidence with { Origin = default },
            "before capture" => evidence with { CapturedAtUtc = Start.AddTicks(-1) },
            "after capture" => evidence with { CapturedAtUtc = Start.AddSeconds(10).AddTicks(1) },
            "missing" => null,
            "unavailable" => throw new InvalidOperationException("Test provider unavailable."),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
        var reader = new TrustedMapContributionPhotoEvidenceReader(new CameraActor(), ports, ports, source, ports.Clock);
        var writer = new WriterProbe();
        var service = new MapContributionSubmissionService(
            new MapContributionSubmissionPreflight(ports, ports, ports, reader, ports.Clock), writer);

        var result = await service.SubmitAsync(AttractionId, SessionId, ports.Observation, PhotoKey, "Description");

        Assert.Equal(MapSubmissionOutcome.Denied, result.Outcome);
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnauthenticatedOrInactiveActorCannotReadPrivateCameraEvidence(bool authenticated, bool active)
    {
        var ports = new TestPorts { AuthorizedOwner = active ? OwnerId : null };
        var source = new CameraSource(() => CameraEvidence());
        var reader = new TrustedMapContributionPhotoEvidenceReader(
            new CameraActor { IsAuthenticated = authenticated }, ports, ports, source, ports.Clock);

        Assert.Equal(MapContributionPhotoVerificationResult.Denied,
            await reader.VerifyAsync(OwnerId, AttractionId, SessionId, PhotoKey));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EvidenceIsDiscardedWhenTheActorChangesOrTheSessionExpiresDuringLookup(bool changeActor)
    {
        var ports = new TestPorts();
        var actor = new CameraActor();
        var source = new CameraSource(() =>
        {
            if (changeActor)
                actor.UserId = ForeignId;
            else
                ports.Clock.Now = Start.AddSeconds(10).AddMinutes(10).AddTicks(1);
            return CameraEvidence();
        });
        var reader = new TrustedMapContributionPhotoEvidenceReader(actor, ports, ports, source, ports.Clock);

        Assert.Equal(MapContributionPhotoVerificationResult.Denied,
            await reader.VerifyAsync(OwnerId, AttractionId, SessionId, PhotoKey));
    }

    private static MapCameraEvidence CameraEvidence() =>
        new(AccountSubject, OwnerId, AttractionId, SessionId, PhotoKey, Start.AddSeconds(5), MapPhotoOrigin.InAppCamera);

    private sealed class CameraActor : ICurrentUser
    {
        public Guid? UserId { get; set; } = AccountSubject;
        public bool IsAuthenticated { get; init; } = true;
    }

    private sealed class CameraSource(Func<MapCameraEvidence?> read) : IMapCameraEvidenceSource
    {
        public int Calls { get; private set; }
        public (Guid Subject, Guid Owner, Guid Attraction, Guid Session) Request { get; private set; }

        public Task<MapCameraEvidence?> FindAsync(Guid accountSubjectId, Guid ownerHobbiesModuleProfileId,
            Guid attractionId, Guid captureSessionId, CancellationToken cancellationToken)
        {
            Calls++;
            Request = (accountSubjectId, ownerHobbiesModuleProfileId, attractionId, captureSessionId);
            return Task.FromResult(read());
        }
    }

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(5_999_999_999L, true)]
    [InlineData(6_000_000_000L, true)]
    [InlineData(6_000_000_001L, false)]
    public async Task SubmissionWindowUsesStoredCompletionAndServerClock(long elapsedTicks, bool expected)
    {
        var ports = new TestPorts();
        ports.Clock.Now = Start.AddSeconds(10).AddTicks(elapsedTicks);
        // Client-supplied timing must neither extend nor shorten eligibility.
        ports.Observation = ports.Observation! with
        {
            CaptureStartedAt = ports.Clock.Now,
            CaptureCompletedAt = ports.Clock.Now
        };
        var writer = new WriterProbe();
        var prepared = await PrepareAndSubmitInTestAsync(ports, ports, writer);
        Assert.Equal(expected, prepared is not null);
        Assert.Equal(expected ? 1 : 0, writer.Calls);
        if (!expected)
            Assert.DoesNotContain("photo", ports.Calls);
    }

    [Fact]
    public async Task ExpiryDuringPhotoVerificationDiscardsLateVerifiedEvidence()
    {
        var ports = new TestPorts();
        var reader = new PendingTestPhotoReader();
        var writer = new WriterProbe();
        var attempt = PrepareAndSubmitInTestAsync(ports, reader, writer);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ports.Clock.Now = Start.AddSeconds(10).AddMinutes(10).AddTicks(1);
        reader.Completion.SetResult(MapContributionPhotoVerificationResult.Verified);
        Assert.Null(await attempt);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task AuthorizedCompleteCapture_WithTrustedTestPhoto_ReturnsOnlyDecisionWithoutWrites()
    {
        var ports = new TestPorts();
        var session = ports.Session!;

        Assert.True(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture", "attraction", "photo" }, ports.Calls);
        Assert.Equal((SessionId, OwnerId, AttractionId), ports.CaptureScope);
        Assert.Equal((OwnerId, AttractionId, SessionId, PhotoKey), ports.PhotoScope);
        Assert.Equal(Start.AddSeconds(10), session.CompletedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizationUnavailable_StopsBeforeReadingRecords(bool emptyOwner)
    {
        var ports = new TestPorts { AuthorizedOwner = emptyOwner ? Guid.Empty : null };

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access" }, ports.Calls);
    }

    [Theory]
    [InlineData("foreign owner")]
    [InlineData("wrong attraction")]
    [InlineData("wrong session")]
    [InlineData("missing")]
    [InlineData("incomplete")]
    public async Task InvalidCapture_ReturnsSameDenialAndStopsBeforeEvidenceReads(string problem)
    {
        var ports = new TestPorts
        {
            Session = problem switch
            {
                "foreign owner" => CreateSession(ownerId: ForeignId),
                "wrong attraction" => CreateSession(attractionId: ForeignId),
                "wrong session" => CreateSession(sessionId: ForeignId),
                "incomplete" => CreateSession(durationSeconds: null),
                _ => null
            }
        };

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture" }, ports.Calls);
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("accuracy")]
    [InlineData("duration")]
    [InlineData("missing attraction")]
    [InlineData("wrong canonical attraction")]
    public async Task InvalidLocationEvidence_StopsBeforePhotoVerification(string problem)
    {
        var ports = new TestPorts();
        switch (problem)
        {
            case "distance":
                ports.Observation = ports.Observation! with { UserLongitude = 0.001 };
                break;
            case "accuracy":
                ports.Observation = ports.Observation! with { HorizontalAccuracyMetres = 31 };
                break;
            case "duration":
                ports.Session = CreateSession(durationSeconds: 121);
                break;
            case "missing attraction":
                ports.Attraction = null;
                break;
            case "wrong canonical attraction":
                ports.Attraction = MapAttraction.Create(ForeignId, "Other attraction", 0, 0);
                break;
        }

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture", "attraction" }, ports.Calls);
    }

    [Theory]
    [InlineData(MapContributionPhotoVerificationResult.Denied)]
    [InlineData((MapContributionPhotoVerificationResult)99)]
    public async Task PhotoVerification_RequiresExplicitVerifiedResult(MapContributionPhotoVerificationResult result)
    {
        var ports = new TestPorts { PhotoResult = result };

        var writer = new WriterProbe();
        Assert.Null(await PrepareAndSubmitInTestAsync(ports, ports, writer));
        Assert.Equal(0, writer.Calls);
        Assert.Equal(new[] { "access", "capture", "attraction", "photo" }, ports.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task MissingPhotoReferenceCannotProducePreparedSubmissionOrCallWriter(string? key)
    {
        var ports = new TestPorts();
        var writer = new WriterProbe();

        Assert.Null(await PrepareAndSubmitInTestAsync(ports, ports, writer, key));
        Assert.Empty(ports.Calls);
        Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData("https://example.invalid/private/photo.jpg")]
    [InlineData("//example.invalid/private/photo.jpg")]
    [InlineData("private\\photo.jpg")]
    [InlineData("file:///private/photo.jpg")]
    public async Task MalformedPrivateReferenceIsDeniedEvenWhenFakeVerifierReturnsVerified(string key)
    {
        var ports = new TestPorts();
        var writer = new WriterProbe();

        Assert.Null(await PrepareAndSubmitInTestAsync(ports, ports, writer, key));
        Assert.Equal(key, ports.PhotoScope.Key);
        Assert.Equal(0, writer.Calls);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("foreign owner", false)]
    [InlineData("wrong attraction", false)]
    [InlineData("wrong session", false)]
    [InlineData("learning", false)]
    [InlineData("social", false)]
    [InlineData("gallery", false)]
    public async Task SyntheticTrustedEvidenceMustMatchEveryScopeBeforePreparation(string scenario, bool expected)
    {
        var ports = new TestPorts();
        var reader = new BoundTestPhotoReader(scenario);
        var writer = new WriterProbe();

        var prepared = await PrepareAndSubmitInTestAsync(ports, reader, writer);
        Assert.Equal((OwnerId, AttractionId, SessionId, PhotoKey), reader.Request);
        Assert.Equal(expected, prepared is not null);
        Assert.Equal(expected ? 1 : 0, writer.Calls);
        Assert.Equal(new[] { "access", "capture", "attraction" }, ports.Calls);
        if (expected)
            Assert.Equal(PhotoKey, prepared!.Contribution.PrivatePhotoStorageObjectKey);
        else
            Assert.Null(prepared); // No owner, media key, location or rejection detail is returned.
    }

    [Fact]
    public async Task VerifiedPhotoCannotAuthorizeASubstitutedObjectOnTheSamePreflightInstance()
    {
        var ports = new TestPorts();
        // Synthetic object binding only. This does not establish camera or storage authenticity.
        var reader = new BoundTestPhotoReader("matching");
        var writer = new WriterProbe();
        var preflight = new MapContributionSubmissionPreflight(ports, ports, ports, reader, ports.Clock);

        async Task<MapContributionSubmissionPreflight.PreparedSubmission?> SubmitInTestAsync(string key)
        {
            var prepared = await preflight.PrepareAsync(
                AttractionId, SessionId, ports.Observation, key, "Description");
            if (prepared is not null)
                await writer.SubmitAsync(prepared);
            return prepared;
        }

        Assert.NotNull(await SubmitInTestAsync(PhotoKey));
        Assert.Equal(1, writer.Calls);
        const string substitutedKey = "private/another-camera-object";
        Assert.Null(await SubmitInTestAsync(substitutedKey));
        Assert.Equal((OwnerId, AttractionId, SessionId, substitutedKey), reader.Request);
        Assert.Equal(2, reader.Calls);
        Assert.Equal(1, writer.Calls);

        var retry = await SubmitInTestAsync(PhotoKey);
        Assert.NotNull(retry);
        Assert.Equal(PhotoKey, retry.Contribution.PrivatePhotoStorageObjectKey);
        Assert.Equal(3, reader.Calls);
        Assert.Equal(2, writer.Calls);
    }

    [Fact]
    public async Task PhotoBoundToOneValidSessionCannotAuthorizeAnotherSessionButAllowsOriginalSessionRetry()
    {
        var ports = new TestPorts();
        var firstSession = CreateSession();
        var secondSession = CreateSession(sessionId: ForeignId);
        Assert.NotEqual(firstSession.SessionId, secondSession.SessionId);
        foreach (var session in new[] { firstSession, secondSession })
        {
            Assert.True(MapContributionEvidenceValidator.Validate(
                ports.Attraction, session, OwnerId, ports.Observation).PassesChecks);
            Assert.True(MapContributionSubmissionWindow.Allows(session, ports.Clock.GetUtcNow()));
        }

        // Synthetic fixture binds P to S1 only; no global consumption or real storage is simulated.
        var reader = new BoundTestPhotoReader("matching");
        var writer = new WriterProbe();
        var preflight = new MapContributionSubmissionPreflight(ports, ports, ports, reader, ports.Clock);

        async Task<MapContributionSubmissionPreflight.PreparedSubmission?> SubmitInTestAsync(
            MapContributionCaptureSession session)
        {
            ports.Session = session;
            ports.Calls.Clear();
            var prepared = await preflight.PrepareAsync(
                AttractionId, session.SessionId, ports.Observation, PhotoKey, "Description");
            Assert.Equal((session.SessionId, OwnerId, AttractionId), ports.CaptureScope);
            Assert.Equal(new[] { "access", "capture", "attraction" }, ports.Calls);
            if (prepared is not null)
                await writer.SubmitAsync(prepared);
            return prepared;
        }

        var first = await SubmitInTestAsync(firstSession);
        Assert.NotNull(first);
        Assert.Equal(firstSession.SessionId, first.Contribution.CaptureSessionId);
        Assert.Equal(PhotoKey, first.Contribution.PrivatePhotoStorageObjectKey);
        Assert.Equal((OwnerId, AttractionId, firstSession.SessionId, PhotoKey), reader.Request);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(1, writer.Calls);

        Assert.Null(await SubmitInTestAsync(secondSession));
        Assert.Equal((OwnerId, AttractionId, secondSession.SessionId, PhotoKey), reader.Request);
        Assert.Equal(2, reader.Calls); // S2 reached verification; invalid capture timing/scope did not cause denial.
        Assert.Equal(1, writer.Calls);

        var retry = await SubmitInTestAsync(firstSession);
        Assert.NotNull(retry);
        Assert.Equal(firstSession.SessionId, retry.Contribution.CaptureSessionId);
        Assert.Equal(PhotoKey, retry.Contribution.PrivatePhotoStorageObjectKey);
        Assert.Equal((OwnerId, AttractionId, firstSession.SessionId, PhotoKey), reader.Request);
        Assert.Equal(3, reader.Calls);
        Assert.Equal(2, writer.Calls);
    }

    [Fact]
    public async Task ProductionDenyAllReaderBlocksOtherwiseValidPreparation()
    {
        var ports = new TestPorts();
        var writer = new WriterProbe();

        Assert.Null(await PrepareAndSubmitInTestAsync(
            ports, new DenyAllMapContributionPhotoEvidenceReader(), writer));
        Assert.Equal(0, writer.Calls);
        Assert.Equal(new[] { "access", "capture", "attraction" }, ports.Calls);
    }

    [Fact]
    public async Task CancellationWhileFakeVerificationIsPendingDiscardsItsLateVerifiedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new TestPorts();
        var reader = new PendingTestPhotoReader();
        var writer = new WriterProbe();
        var attempt = PrepareAndSubmitInTestAsync(ports, reader, writer, cancellationToken: cancellation.Token);
        try
        {
            await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(attempt.IsCompleted);
            Assert.Equal(cancellation.Token, reader.Token);
            cancellation.Cancel();
            reader.Completion.TrySetResult(MapContributionPhotoVerificationResult.Verified);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(0, writer.Calls);
        }
        finally
        {
            reader.Completion.TrySetResult(MapContributionPhotoVerificationResult.Denied);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimulatedStorageFailuresPropagateWithoutPreparedResultOrWriterCall(bool timeout)
    {
        Exception failure = timeout
            ? new TimeoutException("Synthetic storage timeout.")
            : new System.IO.IOException("Synthetic storage failure.");
        var ports = new TestPorts { FailAt = "photo", Failure = failure };
        var writer = new WriterProbe();

        var error = await Assert.ThrowsAsync(failure.GetType(),
            () => PrepareAndSubmitInTestAsync(ports, ports, writer));
        Assert.Same(failure, error);
        Assert.Equal((OwnerId, AttractionId, SessionId, PhotoKey), ports.PhotoScope);
        Assert.Equal(0, writer.Calls);
    }

    // Test-only composition. There is no production submission orchestrator to exercise.
    private static async Task<MapContributionSubmissionPreflight.PreparedSubmission?> PrepareAndSubmitInTestAsync(
        TestPorts ports, IMapContributionPhotoEvidenceReader reader, WriterProbe writer,
        string? key = PhotoKey, CancellationToken cancellationToken = default)
    {
        var prepared = await new MapContributionSubmissionPreflight(ports, ports, ports, reader, ports.Clock)
            .PrepareAsync(AttractionId, SessionId, ports.Observation, key, "Description", cancellationToken);
        if (prepared is not null)
            await writer.SubmitAsync(prepared, cancellationToken);
        return prepared;
    }

    private sealed class WriterProbe : IMapContributionSubmissionWriter
    {
        public int Calls { get; private set; }
        public MapContribution? Contribution { get; private set; }

        public Task<MapSubmissionResult> SubmitAsync(
            MapContributionSubmissionPreflight.PreparedSubmission prepared,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Contribution = prepared.Contribution;
            return Task.FromResult(new MapSubmissionResult(MapSubmissionOutcome.Created, prepared.Contribution.ContributionId));
        }
    }

    // Synthetic metadata only. This double establishes no real camera or storage provenance.
    private sealed class BoundTestPhotoReader(string scenario) : IMapContributionPhotoEvidenceReader
    {
        public int Calls { get; private set; }
        public (Guid Owner, Guid Attraction, Guid Session, string? Key) Request { get; private set; }

        public Task<MapContributionPhotoVerificationResult> VerifyAsync(
            Guid ownerHobbiesModuleProfileId, Guid attractionId, Guid captureSessionId,
            string? privatePhotoStorageObjectKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = (ownerHobbiesModuleProfileId, attractionId, captureSessionId, privatePhotoStorageObjectKey);
            var evidenceOwner = scenario == "foreign owner" ? ForeignId : OwnerId;
            var evidenceAttraction = scenario == "wrong attraction" ? ForeignId : AttractionId;
            var evidenceSession = scenario == "wrong session" ? ForeignId : SessionId;
            var module = scenario is "learning" or "social" ? scenario : "hobbies";
            var source = scenario == "gallery" ? "gallery" : "map-camera";
            var verified = Request == (evidenceOwner, evidenceAttraction, evidenceSession, PhotoKey)
                && module == "hobbies" && source == "map-camera";
            return Task.FromResult(verified
                ? MapContributionPhotoVerificationResult.Verified
                : MapContributionPhotoVerificationResult.Denied);
        }
    }

    private sealed class PendingTestPhotoReader : IMapContributionPhotoEvidenceReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MapContributionPhotoVerificationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public Task<MapContributionPhotoVerificationResult> VerifyAsync(
            Guid ownerHobbiesModuleProfileId, Guid attractionId, Guid captureSessionId,
            string? privatePhotoStorageObjectKey, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    [Fact]
    public async Task MissingRequiredEvidence_StopsBeforeCallingPorts()
    {
        var ports = new TestPorts { Observation = null };

        Assert.False(await ports.CheckAsync());
        Assert.Empty(ports.Calls);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("access", 1)]
    [InlineData("capture", 2)]
    [InlineData("attraction", 3)]
    [InlineData("photo", 4)]
    public async Task Cancellation_PropagatesAndStopsFurtherWork(string? cancelAt, int expectedCalls)
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new TestPorts { CancelAt = cancelAt, Cancellation = cancellation };
        if (cancelAt is null)
            cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.CheckAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(expectedCalls, ports.Calls.Count);
        Assert.All(ports.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Theory]
    [InlineData("access", 1)]
    [InlineData("capture", 2)]
    [InlineData("attraction", 3)]
    [InlineData("photo", 4)]
    public async Task DependencyFailure_PropagatesAndStopsFurtherWork(string failAt, int expectedCalls)
    {
        var ports = new TestPorts { FailAt = failAt };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ports.CheckAsync());
        Assert.Same(ports.Failure, error);
        Assert.Equal(expectedCalls, ports.Calls.Count);
    }

    private static MapContributionCaptureSession CreateSession(
        Guid? ownerId = null, Guid? attractionId = null, Guid? sessionId = null, int? durationSeconds = 10)
    {
        var session = MapContributionCaptureSession.Create(
            sessionId ?? SessionId, ownerId ?? OwnerId, attractionId ?? AttractionId, Start);
        if (durationSeconds is int seconds)
            session.Complete(Start.AddSeconds(seconds));
        return session;
    }

    private sealed class TestPorts : IMapCaptureSessionAccessReader, IMapContributionCaptureSessionRepository,
        IMapAttractionRepository, IMapContributionPhotoEvidenceReader
    {
        public TestClock Clock { get; } = new();
        public Guid? AuthorizedOwner { get; init; } = OwnerId;
        public MapContributionCaptureSession? Session { get; set; } = CreateSession();
        public MapAttraction? Attraction { get; set; } = MapAttraction.Create(AttractionId, "Canonical attraction", 0, 0);
        public MapContributionLocationEvidence? Observation { get; set; } = new(0, 0, null, null, 10, null, null);
        public MapContributionPhotoVerificationResult PhotoResult { get; init; } = MapContributionPhotoVerificationResult.Verified;
        public List<string> Calls { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        public (Guid Session, Guid Owner, Guid Attraction) CaptureScope { get; private set; }
        public (Guid Owner, Guid Attraction, Guid Session, string? Key) PhotoScope { get; private set; }
        public string? CancelAt { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public string? FailAt { get; init; }
        public Exception Failure { get; init; } = new InvalidOperationException("Test dependency failure.");

        public Task<bool> CheckAsync(CancellationToken cancellationToken = default) =>
            new MapContributionSubmissionPreflight(this, this, this, this, Clock)
                .PassesChecksAsync(AttractionId, SessionId, Observation, PhotoKey, cancellationToken);

        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken)
        {
            Assert.Equal(AttractionId, attractionId);
            return Respond("access", AuthorizedOwner, cancellationToken);
        }

        public Task<MapContributionCaptureSession?> FindOwnedAsync(
            Guid sessionId, Guid ownerHobbiesModuleProfileId, Guid attractionId, CancellationToken cancellationToken)
        {
            CaptureScope = (sessionId, ownerHobbiesModuleProfileId, attractionId);
            return Respond("capture", Session, cancellationToken);
        }

        public Task<MapAttraction?> FindByIdAsync(Guid attractionId, CancellationToken cancellationToken)
        {
            Assert.Equal(AttractionId, attractionId);
            return Respond("attraction", Attraction, cancellationToken);
        }

        public Task<MapContributionPhotoVerificationResult> VerifyAsync(
            Guid ownerHobbiesModuleProfileId, Guid attractionId, Guid captureSessionId,
            string? privatePhotoStorageObjectKey, CancellationToken cancellationToken)
        {
            PhotoScope = (ownerHobbiesModuleProfileId, attractionId, captureSessionId, privatePhotoStorageObjectKey);
            return Respond("photo", PhotoResult, cancellationToken);
        }

        private Task<T> Respond<T>(string port, T response, CancellationToken token)
        {
            Calls.Add(port);
            Tokens.Add(token);
            if (port == FailAt)
                return Task.FromException<T>(Failure);
            if (port == CancelAt)
                Cancellation!.Cancel();
            return Task.FromResult(response);
        }

        public Task AddAsync(MapContributionCaptureSession session, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never write capture sessions.");

        public Task<bool> TryCompleteAsync(Guid sessionId, Guid ownerHobbiesModuleProfileId, Guid attractionId,
            DateTimeOffset completedAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never complete capture sessions.");

        public Task AddAsync(MapAttraction attraction, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never write attractions.");

        public Task<IReadOnlyList<MapAttractionDiscoveryItem>> DiscoverAsync(
            MapAttractionDiscoveryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never query attraction discovery.");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start.AddSeconds(10);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
