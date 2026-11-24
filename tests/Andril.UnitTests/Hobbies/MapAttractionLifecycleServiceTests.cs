using System.Reflection;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapAttractionLifecycleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStateTransitionsAndNoOpsReusePrototypeWithoutMutatingOriginal(bool noOp)
    {
        var writer = new SyntheticWriter();
        var original = writer.State!;
        var command = Command(writer, desired: noOp);
        var result = await new MapAttractionLifecycleService(writer).ChangeAsync(command);
        Assert.Equal(noOp ? MapAttractionLifecycleOutcome.Unchanged : MapAttractionLifecycleOutcome.Changed, result.Outcome);
        Assert.Equal(noOp ? 2 : 3, result.Version);
        Assert.Equal(noOp, writer.State!.IsActive);
        Assert.True(original.IsActive);
        Assert.Equal(2, original.Version);
        Assert.Single(writer.Receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRetryReturnsOriginalResultEvenAfterLaterTransition(bool noOp)
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var command = Command(writer, desired: noOp);
        var first = await service.ChangeAsync(command);
        var later = Command(writer, desired: !noOp, expected: writer.State!.Version);
        await service.ChangeAsync(later);
        var replay = await service.ChangeAsync(new(command.RequestId, command.AttractionId, command.IsActive, command.ExpectedVersion));
        Assert.Equal(first with { Outcome = MapAttractionLifecycleOutcome.ExistingAuthorized }, replay);
        Assert.Equal(3, writer.AuthorizationCalls);
        Assert.Equal(2, writer.Receipts.Count);
        Assert.Equal(!noOp, writer.State!.IsActive);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task StaleAndFutureVersionsConflictEvenForMatchingDesiredState(long expected)
    {
        var writer = new SyntheticWriter();
        var result = await new MapAttractionLifecycleService(writer).ChangeAsync(Command(writer, true, expected));
        AssertSafe(result, MapAttractionLifecycleOutcome.Conflict);
        Assert.Equal(2, writer.State!.Version);
        Assert.Empty(writer.Receipts);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("version")]
    [InlineData("attraction")]
    public async Task ChangedRetryCannotSubstituteAcceptedEvidence(string changed)
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var command = Command(writer);
        await service.ChangeAsync(command);
        var retry = new MapAttractionLifecycleChange(command.RequestId,
            changed == "attraction" ? Guid.NewGuid() : command.AttractionId,
            changed == "state" ? true : command.IsActive,
            changed == "version" ? 3 : command.ExpectedVersion);
        var result = await service.ChangeAsync(retry);
        AssertSafe(result, changed == "attraction" ? MapAttractionLifecycleOutcome.Denied : MapAttractionLifecycleOutcome.Conflict);
        Assert.Equal(3, writer.State!.Version);
        Assert.Single(writer.Receipts);
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("denied")]
    [InlineData("empty-subject")]
    public async Task MissingStateOrManagementAuthorizationCannotProduceSuccess(string condition)
    {
        var writer = new SyntheticWriter();
        var command = Command(writer, true);
        if (condition == "missing-state") writer.State = null;
        if (condition == "denied") writer.Authorize = _ => Task.FromResult<string?>(null);
        if (condition == "empty-subject") writer.Authorize = _ => Task.FromResult<string?>(" ");
        AssertSafe(await new MapAttractionLifecycleService(writer).ChangeAsync(command), MapAttractionLifecycleOutcome.Denied);
        Assert.Empty(writer.Receipts);
        if (condition == "missing-state") Assert.Null(writer.State);
        else Assert.Equal(2, writer.State!.Version);
    }

    [Fact]
    public async Task EveryRetryRequiresFreshSyntheticManagementAuthorization()
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var command = Command(writer);
        await service.ChangeAsync(command);
        writer.Authorize = _ => Task.FromResult<string?>(null);
        AssertSafe(await service.ChangeAsync(command), MapAttractionLifecycleOutcome.Denied);
        writer.Authorize = _ => Task.FromResult<string?>("another-synthetic-reviewer");
        AssertSafe(await service.ChangeAsync(command), MapAttractionLifecycleOutcome.Conflict);
        Assert.Single(writer.Receipts);
        Assert.Equal(3, writer.AuthorizationCalls);
    }

    [Fact]
    public async Task PreCancellationAndInvalidInputsNeverInvokeWriter()
    {
        var writer = new StubWriter();
        var service = new MapAttractionLifecycleService(writer);
        Assert.Throws<ArgumentNullException>(() => new MapAttractionLifecycleService(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ChangeAsync(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChangeAsync(new(Guid.NewGuid(), Guid.NewGuid(), false, 0), new(true)));
        Assert.Equal(0, writer.Calls);
        Assert.Throws<ArgumentException>(() => new MapAttractionLifecycleChange(Guid.Empty, Guid.NewGuid(), true, 0));
        Assert.Throws<ArgumentException>(() => new MapAttractionLifecycleChange(Guid.NewGuid(), Guid.Empty, true, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapAttractionLifecycleChange(Guid.NewGuid(), Guid.NewGuid(), true, -1));
    }

    [Fact]
    public async Task CancellationDuringAuthorizationLeavesExplicitStateAndReceiptsUnchanged()
    {
        var writer = new SyntheticWriter();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Authorize = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return "synthetic-reviewer"; };
        var task = new MapAttractionLifecycleService(writer).ChangeAsync(Command(writer), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.True(writer.State!.IsActive);
        Assert.Equal(2, writer.State.Version);
        Assert.Empty(writer.Receipts);
    }

    [Fact]
    public async Task CompetingSyntheticChangesAtOneVersionHaveOneWinner()
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var first = Command(writer);
        var second = Command(writer);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<MapAttractionLifecycleResult> Run(MapAttractionLifecycleChange command)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await start.Task;
            return await service.ChangeAsync(command);
        }
        var a = Run(first);
        var b = Run(second);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult();
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, r => r.Outcome == MapAttractionLifecycleOutcome.Changed);
        Assert.Single(results, r => r.Outcome == MapAttractionLifecycleOutcome.Conflict);
        Assert.Equal(3, writer.State!.Version);
        Assert.Single(writer.Receipts);
    }

    [Fact]
    public async Task CancellationWhileWaitingReleasesTheSyntheticGateWithoutASecondChange()
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Authorize = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "synthetic-reviewer"; };
        var first = service.ChangeAsync(Command(writer));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var second = service.ChangeAsync(Command(writer), cts.Token);
        Assert.False(second.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        Assert.Equal(MapAttractionLifecycleOutcome.Changed, (await first).Outcome);
        Assert.Equal(3, writer.State!.Version);
        Assert.Single(writer.Receipts);
    }

    [Theory]
    [InlineData((int)MapAttractionLifecycleOutcome.Changed)]
    [InlineData((int)MapAttractionLifecycleOutcome.OutcomeUnknown)]
    public async Task LateCancellationDoesNotOverwriteAWriterCommitOutcome(int outcomeValue)
    {
        var outcome = (MapAttractionLifecycleOutcome)outcomeValue;
        var writer = new SyntheticWriter();
        using var cts = new CancellationTokenSource();
        writer.AfterApply = () => { cts.Cancel(); return outcome; };
        var result = await new MapAttractionLifecycleService(writer).ChangeAsync(Command(writer), cts.Token);
        Assert.Equal(outcome, result.Outcome);
        Assert.False(writer.State!.IsActive);
        Assert.Single(writer.Receipts);
        Assert.Equal(1, writer.Calls);
        if (outcome == MapAttractionLifecycleOutcome.OutcomeUnknown) AssertSafe(result, outcome);
    }

    [Fact]
    public async Task LostAcknowledgementDoesNotRetryAndExplicitLaterRetryUsesOriginalReceipt()
    {
        var writer = new SyntheticWriter();
        var service = new MapAttractionLifecycleService(writer);
        var command = Command(writer);
        writer.AfterApply = () => throw new InvalidOperationException("private reviewer audit database details");
        AssertSafe(await service.ChangeAsync(command), MapAttractionLifecycleOutcome.OutcomeUnknown);
        Assert.Equal(1, writer.Calls);
        writer.AfterApply = null;
        var replay = await service.ChangeAsync(command);
        Assert.Equal(MapAttractionLifecycleOutcome.ExistingAuthorized, replay.Outcome);
        Assert.Equal(3, replay.Version);
        Assert.Equal(3, writer.State!.Version);
        Assert.Single(writer.Receipts);
    }

    [Theory]
    [InlineData((int)MapAttractionLifecycleOutcome.Denied)]
    [InlineData((int)MapAttractionLifecycleOutcome.Conflict)]
    [InlineData((int)MapAttractionLifecycleOutcome.Unavailable)]
    [InlineData((int)MapAttractionLifecycleOutcome.OutcomeUnknown)]
    public async Task NonSuccessResultsDiscardAllDependencyMetadata(int outcomeValue)
    {
        var outcome = (MapAttractionLifecycleOutcome)outcomeValue;
        var writer = new StubWriter { Run = (_, _) => Task.FromResult(new MapAttractionLifecycleResult(outcome, Guid.NewGuid(), Guid.NewGuid(), true, 937)) };
        AssertSafe(await new MapAttractionLifecycleService(writer).ChangeAsync(new(Guid.NewGuid(), Guid.NewGuid(), false, 2)), outcome);
        Assert.Equal(1, writer.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("request")]
    [InlineData("attraction")]
    [InlineData("state")]
    [InlineData("missing-version")]
    [InlineData("negative-version")]
    [InlineData("changed-no-increment")]
    [InlineData("unchanged-increment")]
    [InlineData("replay-future")]
    [InlineData("overflow")]
    public async Task UnrecognizedOrContradictoryWriterEvidenceCannotBecomeSuccess(string shape)
    {
        var writer = new StubWriter();
        var command = new MapAttractionLifecycleChange(Guid.NewGuid(), Guid.NewGuid(), false, shape == "overflow" ? long.MaxValue : 2);
        writer.Run = (c, _) =>
        {
            var result = new MapAttractionLifecycleResult(MapAttractionLifecycleOutcome.Changed, c.RequestId, c.AttractionId, c.IsActive, 3);
            result = shape switch
            {
                "null" => null!,
                "unknown" => result with { Outcome = (MapAttractionLifecycleOutcome)99 },
                "request" => result with { RequestId = Guid.NewGuid() },
                "attraction" => result with { AttractionId = Guid.NewGuid() },
                "state" => result with { IsActive = true },
                "missing-version" => result with { Version = null },
                "negative-version" => result with { Version = -1 },
                "changed-no-increment" => result with { Version = 2 },
                "unchanged-increment" => result with { Outcome = MapAttractionLifecycleOutcome.Unchanged },
                "replay-future" => result with { Outcome = MapAttractionLifecycleOutcome.ExistingAuthorized, Version = 4 },
                _ => result with { Version = long.MinValue }
            };
            return Task.FromResult(result);
        };
        AssertSafe(await new MapAttractionLifecycleService(writer).ChangeAsync(command), MapAttractionLifecycleOutcome.OutcomeUnknown);
        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task UnexpectedWriterFailureIsPrivateAndNeverAutomaticallyRetried()
    {
        var writer = new StubWriter { Run = (_, _) => throw new InvalidOperationException("secret reviewer private/photo/key server=password") };
        AssertSafe(await new MapAttractionLifecycleService(writer).ChangeAsync(new(Guid.NewGuid(), Guid.NewGuid(), false, 0)), MapAttractionLifecycleOutcome.OutcomeUnknown);
        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task VersionOverflowCannotWrapSyntheticStateAndNoOpAtMaximumStillWorks()
    {
        var writer = new SyntheticWriter();
        writer.State = MapAttractionStatePrototype.CreateSnapshot(writer.Attraction, true, long.MaxValue);
        var service = new MapAttractionLifecycleService(writer);
        AssertSafe(await service.ChangeAsync(Command(writer, false, long.MaxValue)), MapAttractionLifecycleOutcome.Conflict);
        Assert.Empty(writer.Receipts);
        var unchanged = await service.ChangeAsync(Command(writer, true, long.MaxValue));
        Assert.Equal(MapAttractionLifecycleOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(long.MaxValue, unchanged.Version);
        Assert.True(writer.State.IsActive);
    }

    [Fact]
    public void CommandIsImmutableAndContainsNoTrustedActorOrPermissionClaims()
    {
        var properties = typeof(MapAttractionLifecycleChange).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(new[] { "AttractionId", "EqualityContract", "ExpectedVersion", "IsActive", "RequestId" }, properties.Select(p => p.Name).Order());
        Assert.All(properties, p => Assert.Null(p.SetMethod));
        Assert.False(typeof(MapAttractionLifecycleService).IsPublic);
        Assert.False(typeof(IMapAttractionLifecycleWriter).IsPublic);
    }

    private static MapAttractionLifecycleChange Command(SyntheticWriter writer, bool desired = false, long expected = 2)
        => new(Guid.NewGuid(), writer.Attraction.AttractionId, desired, expected);

    private static void AssertSafe(MapAttractionLifecycleResult result, MapAttractionLifecycleOutcome expected)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.RequestId);
        Assert.Null(result.AttractionId);
        Assert.Null(result.IsActive);
        Assert.Null(result.Version);
    }

    private sealed class StubWriter : IMapAttractionLifecycleWriter
    {
        public int Calls { get; private set; }
        public Func<MapAttractionLifecycleChange, CancellationToken, Task<MapAttractionLifecycleResult>> Run { get; set; }
            = (_, _) => throw new NotSupportedException();
        public Task<MapAttractionLifecycleResult> ChangeAsync(MapAttractionLifecycleChange change, CancellationToken token = default)
        { Calls++; return Run(change, token); }
    }

    // Explicit synthetic in-memory adapter. No durable receipts, database transactions, audit,
    // M2 permissions, production submission coordination or revocation guarantees are proved.
    private sealed class SyntheticWriter : IMapAttractionLifecycleWriter
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public MapAttraction Attraction { get; } = MapAttraction.Create(Guid.NewGuid(), "Synthetic lifecycle", 0, 0);
        public MapAttractionStatePrototype? State { get; set; }
        public Dictionary<(string Subject, Guid Request), (MapAttractionLifecycleChange Change, MapAttractionLifecycleResult Result)> Receipts { get; } = [];
        public Func<CancellationToken, Task<string?>> Authorize { get; set; } = _ => Task.FromResult<string?>("synthetic-reviewer");
        public Func<MapAttractionLifecycleOutcome>? AfterApply { get; set; }
        public int Calls { get; private set; }
        public int AuthorizationCalls { get; private set; }
        public SyntheticWriter() => State = MapAttractionStatePrototype.CreateSnapshot(Attraction, true, 2);
        public async Task<MapAttractionLifecycleResult> ChangeAsync(MapAttractionLifecycleChange command, CancellationToken token = default)
        {
            Calls++;
            await _gate.WaitAsync(token);
            try
            {
                AuthorizationCalls++;
                var subject = await Authorize(token);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(subject) || State is null || command.AttractionId != State.AttractionId)
                    return new(MapAttractionLifecycleOutcome.Denied);
                if (Receipts.TryGetValue((subject, command.RequestId), out var receipt))
                    return receipt.Change == command ? receipt.Result with { Outcome = MapAttractionLifecycleOutcome.ExistingAuthorized }
                        : new(MapAttractionLifecycleOutcome.Conflict);
                if (State.Version != command.ExpectedVersion) return new(MapAttractionLifecycleOutcome.Conflict);
                MapAttractionStatePrototype next;
                try { next = State.Transition(command.IsActive, command.ExpectedVersion); }
                catch (OverflowException) { return new(MapAttractionLifecycleOutcome.Conflict); }
                token.ThrowIfCancellationRequested();
                var accepted = new MapAttractionLifecycleResult(ReferenceEquals(next, State)
                    ? MapAttractionLifecycleOutcome.Unchanged : MapAttractionLifecycleOutcome.Changed,
                    command.RequestId, command.AttractionId, next.IsActive, next.Version);
                State = next;
                Receipts.Add((subject, command.RequestId), (command, accepted));
                var outcome = AfterApply?.Invoke();
                return outcome is null ? accepted : accepted with { Outcome = outcome.Value };
            }
            finally { _gate.Release(); }
        }
    }
}
