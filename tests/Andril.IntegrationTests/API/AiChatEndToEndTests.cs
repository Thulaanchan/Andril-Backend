using System.Security.Cryptography;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Andril.AI.Services;
using Xunit;

namespace Andril.IntegrationTests.API;

/// <summary>Composition of accessible production preparation/codec/policies only. Internal
/// transport/mapper/orchestrator grant access ONLY to UnitTests: no reflection execution or
/// visibility bypass. Their real simulated HTTP composition remains in the existing AI regression.</summary>
public sealed class AiChatEndToEndTests
{
    private sealed class Authorization(AiAuthorizedActor actor) : IAiActorAuthorization
    {
        internal AiAuthorizedActor? Current = actor;
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken t = default) => Task.FromResult(Current);
    }
    private sealed class Gate : IAiPrivacyNoticeGate
    {
        internal AiPrivacyNoticeGateOutcome Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken t = default) => Task.FromResult(Outcome);
    }
    private sealed class Counter : IAiTokenCounter
    {
        internal int Calls;
        internal AiPreparedProviderPayload? Retained;
        internal Action? AfterCount;
        internal bool Missing;
        public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest r, AiPreparedProviderPayload p, CancellationToken t = default)
        {
            t.ThrowIfCancellationRequested(); Calls++; Retained = p; AfterCount?.Invoke();
            return Task.FromResult(Missing ? new(AiTokenCountState.Unavailable) : new AiTokenCountResult(AiTokenCountState.Measured,
                new(r.Model, r.Fingerprint, 400, 400, 400, 400, 400, 2000, 500, true, true), new("synthetic-not-authoritative", "test-v1", r.FramingRevision), p.Binding));
        }
    }
    [Theory]
    [InlineData(AiModule.Learning, "English question")]
    [InlineData(AiModule.Hobbies, "தமிழில் கேள்வி")]
    [InlineData(AiModule.Social, "Tanglish விளக்கம்")]
    [InlineData(AiModule.Learning, "සිංහල ප්‍රශ්නය")]
    public async Task AuthorizedUnsavedPreparationRetainsExactBytesButUnverifiedCompletionIsWithheld(AiModule module, string text)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
        var auth = new Authorization(actor); var privacy = new AiPrivacyNoticeGuard(auth, new Gate());
        await privacy.RequireSameActorSatisfiedAsync(actor);
        var context = AiPreAdmissionPromptContext.ForNewConversation(actor, Guid.NewGuid(), Guid.NewGuid());
        var counter = new Counter(); OpenAiPreparedResponsesRequest? bytes = null;
        var prepared = await AiPromptFactory.PrepareBeforeAdmissionAsync(context, Guid.NewGuid(), text, 100, async (prompt, token) =>
        {
            var r = AiTokenCountRequest.FromPrompt(OpenAiResponsesRequestSerializer.ApprovedModel, OpenAiResponsesRequestSerializer.SerializationRevision, prompt);
            var candidate = AiProviderRequestCandidate.FromMeasuredRequest(r, new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false));
            bytes = OpenAiResponsesRequestSerializer.Prepare(r, candidate);
            var count = await AiTokenCountContract.CountVerifiedAsync(counter, r, bytes.MeasurementPayload, token);
            Assert.Equal(AiProviderRequestValidationOutcome.ValidatedCandidate, AiProviderRequestPolicy.Validate(r, candidate, count, bytes));
            return new(r, bytes.MeasurementPayload, count);
        });
        await privacy.RequireSameActorSatisfiedAsync(actor);
        Assert.Same(counter.Retained, bytes!.MeasurementPayload); Assert.Equal(1, counter.Calls);
        Assert.Equal(text, Assert.Single(prepared.Prompt.Messages).Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes.CopyUtf8Payload())), prepared.Measurement.Result.PayloadBinding!.PayloadSha256);
        using var json = JsonDocument.Parse(bytes.CopyUtf8Payload());
        Assert.Equal(text, json.RootElement.GetProperty("input")[1].GetProperty("content").GetString());
        Assert.Empty(json.RootElement.GetProperty("tools").EnumerateArray());
        Assert.False(prepared.MatchesCurrentContext(actor, context.ConversationId, Guid.NewGuid()));
        var response = OpenAiResponsesResponseParser.Parse(JsonSerializer.Serialize(new
        {
            id = "resp_synthetic", @object = "response", model = OpenAiResponsesRequestSerializer.ApprovedModel, status = "completed",
            output = new[] { new { id = "msg_synthetic", type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text = "Uncommitted synthetic candidate" } } } },
            usage = new { input_tokens = 100, output_tokens = 20, total_tokens = 120, output_tokens_details = new { reasoning_tokens = 5 } }
        }), OpenAiResponsesRequestSerializer.ApprovedModel);
        Assert.Equal(OpenAiResponseParseOutcome.Parsed, response.Outcome); Assert.NotNull(response.AssistantText);
        Assert.Equal(OpenAiUsageObservationState.Observed, response.UsageState);
        var decision = response.ValidateUnverifiedText(new(prepared.Measurement.Request.Model, prepared.Measurement.Request.Fingerprint, false));
        Assert.Equal(AiTextValidationOutcome.Invalid, decision.Outcome); Assert.Null(decision.Text);
        // This test intentionally stops: no transaction, dispatch claim, HTTP call, storage,
        // financial settlement or response delivery can be authorized by the prepared record.
    }

    [Theory]
    [InlineData("privacy")] [InlineData("foreign")] [InlineData("measurement")] [InlineData("cancel")]
    public async Task MissingPrerequisitesStopBeforeAnyExecutingBoundary(string defect)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var auth = new Authorization(actor); var gate = new Gate(); var guard = new AiPrivacyNoticeGuard(auth, gate);
        if (defect == "privacy") gate.Outcome = AiPrivacyNoticeGateOutcome.Denied;
        if (defect == "foreign") auth.Current = actor with { ModuleProfileId = Guid.NewGuid() };
        if (defect is "privacy" or "foreign")
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireSameActorSatisfiedAsync(actor)); return; }
        var counter = new Counter { Missing = defect == "measurement" };
        using var cts = new CancellationTokenSource(); if (defect == "cancel") cts.Cancel();
        var r = new AiTokenCountRequest(OpenAiResponsesRequestSerializer.ApprovedModel, OpenAiResponsesRequestSerializer.SerializationRevision, "System", [], "User");
        var p = OpenAiResponsesRequestSerializer.Prepare(r, AiProviderRequestCandidate.FromMeasuredRequest(r, new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false)));
        var ex = await Record.ExceptionAsync(() => AiTokenCountContract.CountVerifiedAsync(counter, r, p.MeasurementPayload, cts.Token));
        Assert.NotNull(ex); Assert.Equal(defect == "cancel" ? 0 : 1, counter.Calls);
    }

    [Fact]
    public async Task AccessRevocationWhileCountingPreventsFurtherAuthorizedProcessing()
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var auth = new Authorization(actor); var privacy = new AiPrivacyNoticeGuard(auth, new Gate());
        await privacy.RequireSameActorSatisfiedAsync(actor);
        var counter = new Counter { AfterCount = () => auth.Current = null };
        var request = new AiTokenCountRequest(OpenAiResponsesRequestSerializer.ApprovedModel,
            OpenAiResponsesRequestSerializer.SerializationRevision, "Synthetic system", [], "Current");
        var payload = OpenAiResponsesRequestSerializer.Prepare(request, AiProviderRequestCandidate.FromMeasuredRequest(request,
            new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false)));
        await AiTokenCountContract.CountVerifiedAsync(counter, request, payload.MeasurementPayload);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => privacy.RequireSameActorSatisfiedAsync(actor));
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void SameAttemptAndUncertaintyPoliciesDoNotGrantRedispatchOrQuotaRelease()
    {
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var op = new AiOperationReference(new(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning), Guid.NewGuid(), Guid.NewGuid());
        var attempt = Guid.NewGuid();
        var reserved = new AiOperationSnapshot(op, AiOperationStatus.Reserved, AiQuotaState.Reserved, AiDispatchState.NotDispatched,
            now, DateOnly.FromDateTime(now.UtcDateTime), Guid.NewGuid(), attempt);
        var active = reserved with { Status = AiOperationStatus.InProgress, Dispatch = AiDispatchState.Confirmed, Revision = Guid.NewGuid() };
        Assert.True(AiOperationPolicy.CanTransition(reserved, active));
        Assert.False(AiOperationPolicy.CanTransition(reserved, active with { AttemptId = Guid.NewGuid() }));
        var uncertain = active with { Status = AiOperationStatus.PendingReconciliation, Dispatch = AiDispatchState.Uncertain };
        var cancelled = AiOperationPolicy.ApplyAccounting(uncertain, AiAccountingEvent.UserCancellation);
        Assert.Equal(AiQuotaState.Reserved, cancelled.Quota); Assert.Equal(attempt, cancelled.AttemptId);
        Assert.Equal(AiRetryDecision.ReconciliationRequired, AiOperationPolicy.CanRetry(op, uncertain, true, true, true));
    }
}
