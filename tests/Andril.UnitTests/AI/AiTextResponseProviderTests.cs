using System.Text;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Synthetic local measurements only, never approved tokenizer/dispatch evidence.
/// Positive HTTP scenarios stay in existing transport/integration tests, not this blocked adapter.</summary>
public sealed class AiTextResponseProviderTests
{
    internal const string Private = "synthetic-private-input-credential-தமிழ்";
    internal static AiAuthorizedActor Actor() => new(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
    internal static AiConstructedPrompt Prompt(string text = Private) => new(Actor(), Guid.NewGuid(),
        Guid.NewGuid(), "Synthetic private system", new[] { new AiPromptMessage(AiStoredMessageRole.User, text) });
    internal static AiTokenCountResult Measurement(AiTokenCountRequest r, AiPreparedProviderPayload p, int total = 2000) =>
        new(AiTokenCountState.Measured, new(r.Model, r.Fingerprint, total - 2, 1, 0, 1, 0, total, 500, true, true),
            new("synthetic-fixture-only", "test-v1", r.FramingRevision), p.Binding);
    internal sealed class Counter : IAiTokenCounter
    {
        internal int Calls;
        internal AiPreparedProviderPayload? Payload;
        internal Func<AiTokenCountRequest, AiPreparedProviderPayload, CancellationToken, Task<AiTokenCountResult>> Count =
            (r, p, _) => Task.FromResult(Measurement(r, p));
        public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest r, AiPreparedProviderPayload p, CancellationToken t = default)
        { Calls++; Payload = p; return Count(r, p, t); }
    }

    [Fact]
    public async Task PreparationRetainsExactBytesProvenanceAndLogicalScopeWithoutGrantingDispatch()
    {
        var counter = new Counter();
        var adapter = new AiTextResponseProvider(counter);
        var prompt = Prompt();
        var prepared = await adapter.PrepareAsync(prompt);
        Assert.Same(counter.Payload, prepared.Payload.MeasurementPayload);
        Assert.Equal(prompt.Fingerprint, prepared.Measurement.PayloadBinding!.LogicalPromptFingerprint);
        Assert.Equal(prepared.Payload.PayloadSha256, prepared.Measurement.PayloadBinding.PayloadSha256);
        Assert.Equal(2000, prepared.Measurement.Evidence!.FullInputTokens);
        Assert.Equal(500, prepared.Measurement.Evidence.MaxBillableOutputTokens);
        Assert.Equal("synthetic-fixture-only", prepared.Measurement.Provenance!.CounterId);
        var bytes = prepared.Payload.CopyUtf8Payload(); bytes[0] ^= 1;
        Assert.False(prepared.Payload.MatchesPayload(bytes));
        Assert.Equal(1, counter.Calls);
        Assert.DoesNotContain(Private, prepared.ToString());
        Assert.DoesNotContain(prompt.Fingerprint, prepared.ToString());
        Assert.False(typeof(IAiResponseProvider).IsAssignableFrom(typeof(AiTextResponseProvider)));
        Assert.False(typeof(AiTextResponseProvider).IsPublic);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("unreliable")]
    [InlineData("estimate")]
    [InlineData("negative")]
    [InlineData("components")]
    [InlineData("over-limit")]
    [InlineData("output")]
    [InlineData("provenance")]
    [InlineData("stale")]
    [InlineData("payload")]
    [InlineData("configuration")]
    public async Task InvalidMeasurementNeverBecomesPreparedRequest(string defect)
    {
        var counter = new Counter { Count = (r, p, _) =>
        {
            var good = Measurement(r, p);
            AiTokenCountResult? bad = defect switch
            {
                "missing" => null,
                "unknown" => new(AiTokenCountState.Unknown),
                "unreliable" => new(AiTokenCountState.Unreliable),
                "estimate" => good with { State = AiTokenCountState.Estimated },
                "negative" => good with { Evidence = good.Evidence! with { SystemTokens = -1 } },
                "components" => good with { Evidence = good.Evidence! with { FullInputTokens = 1999 } },
                "over-limit" => Measurement(r, p, 2001),
                "output" => good with { Evidence = good.Evidence! with { MaxBillableOutputTokens = 501 } },
                "provenance" => good with { Provenance = good.Provenance! with { CounterRevision = "" } },
                "stale" => good with { Evidence = good.Evidence! with { RequestFingerprint = new string('A', 64) } },
                "payload" => good with { PayloadBinding = p.Binding with { PayloadSha256 = new string('B', 64) } },
                _ => good with { PayloadBinding = p.Binding with { ExecutionConfiguration = "other" } }
            };
            return Task.FromResult(bad!);
        } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AiTextResponseProvider(counter).PrepareAsync(Prompt()));
        Assert.DoesNotContain(Private, error.ToString());
        Assert.Equal(1, counter.Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1999)]
    [InlineData(2000)]
    public async Task SyntheticValidCountsRemainNonAuthoritative(int count)
    {
        var counter = new Counter { Count = (r, p, _) => Task.FromResult(Measurement(r, p, count)) };
        var prepared = await new AiTextResponseProvider(counter).PrepareAsync(Prompt());
        var attempt = new AiGenerationAttemptReference(new(Actor(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var state = new AiOperationSnapshot(attempt.Operation, AiOperationStatus.Reserved, AiQuotaState.Reserved,
            AiDispatchState.NotDispatched, now, DateOnly.FromDateTime(now.UtcDateTime), Guid.NewGuid(), attempt.AttemptId);
        Assert.Equal(AiTextExecutionBlock.DispatchAuthorityUnavailable,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared, attempt, state));
        Assert.Equal(AiTextExecutionBlock.DispatchAuthorityUnavailable,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared, attempt,
                state with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed }));
        Assert.Equal(AiTextExecutionBlock.InvalidAttempt,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared, attempt with { AttemptId = Guid.NewGuid() }, state));
        Assert.Equal(AiTextExecutionBlock.InvalidPreparation,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared with { Measurement = null! }, attempt, state));
        Assert.Equal(AiTextExecutionBlock.ReconciliationRequired,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared, attempt,
                state with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain }));
        Assert.Equal(AiTextExecutionBlock.TerminalOperation,
            AiTextResponseProvider.CheckExecutionPrerequisites(prepared, attempt,
                state with { Status = AiOperationStatus.Completed, Dispatch = AiDispatchState.Confirmed, Quota = AiQuotaState.Committed }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringCountingNeverReturnsPreparation(bool during)
    {
        using var cts = new CancellationTokenSource();
        var counter = new Counter { Count = (r, p, t) => { Assert.Equal(cts.Token, t); cts.Cancel(); return Task.FromResult(Measurement(r, p)); } };
        if (!during) cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiTextResponseProvider(counter).PrepareAsync(Prompt(), cts.Token));
        Assert.Equal(during ? 1 : 0, counter.Calls);
    }

    [Fact]
    public async Task CounterExceptionsAreMinimizedAndNeverRetried()
    {
        var counter = new Counter { Count = (_, _, _) => throw new InvalidOperationException(Private) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AiTextResponseProvider(counter).PrepareAsync(Prompt()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Private, error.ToString());
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task ProductionBoundaryCannotUseSyntheticFallback()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiTextResponseProvider(new AiProviderTokenCounter()).PrepareAsync(Prompt()));
        Assert.All(typeof(AiTextResponseProvider).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            c => Assert.Equal(typeof(IAiTokenCounter), Assert.Single(c.GetParameters()).ParameterType));
    }
}
