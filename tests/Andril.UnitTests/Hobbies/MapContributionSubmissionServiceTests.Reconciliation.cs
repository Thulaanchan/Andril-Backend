using System.Reflection;
using System.Text.Json;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed partial class MapContributionSubmissionServiceTests
{
    [Fact]
    public async Task Reconciliation_ReturnsOnlyExactCommittedIdentifierWithoutAnotherSubmission()
    {
        var (f, evidence, ports) = await ArrangeReconciliation();
        using var cancellation = new CancellationTokenSource();
        var committedId = ports.Row!.ContributionId;
        var result = await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence, cancellation.Token);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committedId), result);
        Assert.NotEqual(Assert.Single(f.Requests).Contribution.ContributionId, committedId);
        Assert.Equal((f.Session.SessionId, f.Owner, f.Attraction.AttractionId), Assert.Single(ports.Requests));
        Assert.Equal(cancellation.Token, ports.Token);
        Assert.Equal(1, f.Calls.Count(call => call == "access"));
        Assert.DoesNotContain(Fixture.PhotoKey, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_MissingOriginalOrCommittedEvidenceCannotInferSuccess(bool missingOriginal)
    {
        var (f, evidence, ports) = await ArrangeReconciliation();
        if (!missingOriginal) ports.Row = null;
        var result = await new MapContributionSubmissionReconciliationService(ports, ports)
            .ReconcileAsync(missingOriginal ? null : evidence);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), result);
        Assert.Equal(missingOriginal ? 0 : 1, ports.Requests.Count);
        Assert.Single(f.Requests); // The earlier synthetic Created result is not committed-read proof.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("empty")]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("hobbies")]
    [InlineData("foreign")]
    public async Task Reconciliation_InvalidOrForeignActorIsDeniedBeforeLookup(string invalid)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        ports.Actor = invalid switch
        {
            "missing" => null,
            "inactive" => ports.Actor! with { IsActive = false },
            "empty" => ports.Actor! with { ModuleProfileId = Guid.Empty },
            "foreign" => ports.Actor! with { ModuleProfileId = Guid.NewGuid() },
            _ => ports.Actor! with { Module = invalid }
        };
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied),
            await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence));
        Assert.Empty(ports.Requests);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("attraction")]
    [InlineData("session")]
    [InlineData("photo")]
    [InlineData("description")]
    [InlineData("coordinate")]
    [InlineData("capture-time")]
    public async Task Reconciliation_ContradictoryScopedOrPayloadEvidenceIsDeniedWithoutIdentifier(string changed)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        var row = ports.Row!;
        ports.Row = MapContribution.Create(row.ContributionId,
            changed == "owner" ? Guid.NewGuid() : row.OwnerHobbiesModuleProfileId,
            changed == "attraction" ? Guid.NewGuid() : row.AttractionId,
            changed == "session" ? Guid.NewGuid() : row.CaptureSessionId,
            changed == "photo" ? "private/substituted" : row.PrivatePhotoStorageObjectKey,
            changed == "description" ? "Changed" : row.Description,
            changed switch
            {
                "coordinate" => row.LocationEvidence with { UserLatitude = 1 },
                "capture-time" => row.LocationEvidence with { CaptureCompletedAt = row.LocationEvidence.CaptureCompletedAt!.Value.AddTicks(1) },
                _ => row.LocationEvidence
            }, row.SubmittedAtUtc);
        var result = await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), result);
        Assert.DoesNotContain(row.ContributionId.ToString(), JsonSerializer.Serialize(result));
        Assert.DoesNotContain(row.PrivatePhotoStorageObjectKey, result.ToString());
        Assert.Single(ports.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_CorruptIdentifierOrUnsupportedStateCannotReportSuccess(bool unsupportedState)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        typeof(MapContribution).GetField(unsupportedState ? "<ModerationStatus>k__BackingField" : "<ContributionId>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ports.Row,
                unsupportedState ? (object)(MapContributionModerationStatus)99 : Guid.Empty);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown),
            await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("foreign")]
    [InlineData("module")]
    public async Task Reconciliation_ActorChangesDuringReadDiscardCommittedEvidence(string change)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        ports.AfterRead = () => ports.Actor = change switch
        {
            "revoked" => null,
            "foreign" => ports.Actor! with { ModuleProfileId = Guid.NewGuid() },
            _ => ports.Actor! with { Module = "Social" }
        };
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied),
            await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence));
        Assert.Single(ports.Requests);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("initial-actor")]
    [InlineData("reader")]
    [InlineData("final-actor")]
    public async Task Reconciliation_CancellationAtEveryBoundaryPropagates(string stage)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        using var cancellation = new CancellationTokenSource();
        if (stage == "before") cancellation.Cancel();
        if (stage == "reader") ports.AfterRead = cancellation.Cancel;
        ports.OnActorRead = read =>
        {
            if ((stage == "initial-actor" && read == 1) || (stage == "final-actor" && read == 2))
            {
                ports.Actor = null;
                cancellation.Cancel();
            }
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(stage is "reader" or "final-actor" ? 1 : 0, ports.Requests.Count);
    }

    [Fact]
    public async Task Reconciliation_CancellationDuringAwaitedReadDoesNotRetryOrReturnEvidence()
    {
        var (f, evidence, ports) = await ArrangeReconciliation();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ports.Read = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ports.Row;
        };
        var attempt = new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(ports.Requests);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("reader")]
    public async Task Reconciliation_UnavailableDependenciesReturnUnknownWithoutPrivateDetails(string dependency)
    {
        var (_, evidence, ports) = await ArrangeReconciliation();
        if (dependency == "actor") ports.OnActorRead = _ => throw new IOException("sensitive " + Fixture.PhotoKey);
        else ports.Read = _ => throw new IOException("sensitive " + Fixture.PhotoKey);
        var result = await new MapContributionSubmissionReconciliationService(ports, ports).ReconcileAsync(evidence);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), result);
        Assert.DoesNotContain(Fixture.PhotoKey, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("sensitive", result.ToString());
    }

    [Fact]
    public async Task Reconciliation_OnlyExplicitSubsequentReadsCanObserveALaterCommit()
    {
        var (f, evidence, ports) = await ArrangeReconciliation();
        var committed = ports.Row;
        ports.Row = null;
        var service = new MapContributionSubmissionReconciliationService(ports, ports);
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), await service.ReconcileAsync(evidence));
        Assert.Single(ports.Requests);
        ports.Row = committed;
        Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, committed!.ContributionId),
            await service.ReconcileAsync(evidence));
        Assert.Equal(2, ports.Requests.Count);
        Assert.Single(f.Requests);
        Assert.Equal(1, f.Calls.Count(call => call == "access"));
    }

    private static async Task<(Fixture Fixture, MapSubmissionOriginalEvidence Evidence, ReconciliationPorts Ports)> ArrangeReconciliation()
    {
        var f = new Fixture();
        var attempt = await f.Attempt();
        var prepared = Assert.Single(f.Requests).Contribution;
        var committed = MapContribution.Create(Guid.NewGuid(), f.Owner, prepared.AttractionId, prepared.CaptureSessionId,
            prepared.PrivatePhotoStorageObjectKey, prepared.Description, prepared.LocationEvidence, prepared.SubmittedAtUtc.AddDays(1));
        return (f, attempt.OriginalEvidence!, new(new(f.Owner, "Hobbies", true), committed));
    }

    private sealed class ReconciliationPorts(GalleryActor? actor, MapContribution? row) : IMapContributionCommittedReader, IGalleryActorContext
    {
        internal GalleryActor? Actor { get; set; } = actor;
        internal MapContribution? Row { get; set; } = row;
        internal List<(Guid Session, Guid Owner, Guid Attraction)> Requests { get; } = [];
        internal CancellationToken Token { get; private set; }
        internal Action? AfterRead { get; set; }
        internal Action<int>? OnActorRead { get; set; }
        internal Func<CancellationToken, Task<MapContribution?>>? Read { get; set; }
        private int _actorReads;
        public GalleryActor? Current { get { OnActorRead?.Invoke(++_actorReads); return Actor; } }
        public async Task<MapContribution?> FindOwnedByCaptureSessionAsync(Guid session, Guid owner, Guid attraction, CancellationToken token)
        {
            Requests.Add((session, owner, attraction));
            Token = token;
            var found = Read is null ? Row : await Read(token);
            AfterRead?.Invoke();
            return found;
        }
    }
}
