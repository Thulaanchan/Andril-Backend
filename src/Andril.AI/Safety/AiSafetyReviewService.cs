using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;

namespace Andril.AI.Safety;

/// <summary>Scoped coordination, no classifier, history access, reviewer grants or enforcement.
/// Clearance only permits further checks; never dispatch, persistence, delivery, refund or retention.
/// Subject/assessment freshness must ALSO be verified transactionally by the gateway and at use.</summary>
public sealed class AiSafetyReviewService
{
    private readonly IAiActorAuthorization _authorization;
    private readonly AiPrivacyNoticeGuard _privacy;
    private readonly IAiSafetyReviewGateway _gateway;

    public AiSafetyReviewService(IAiActorAuthorization authorization, AiPrivacyNoticeGuard privacy, IAiSafetyReviewGateway gateway)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public Task<AiSafetyAssessmentResult> AssessInputAsync(AiSafetySubject subject, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSubject(subject, AiSafetyStage.UserInput);
        if (string.IsNullOrWhiteSpace(text) || new StringInfo(text).LengthInTextElements > AiPrototypeLimits.MaxUserTextElements)
            throw new ArgumentException("Invalid AI input subject.", nameof(text));
        return AssessAsync(subject, text, cancellationToken);
    }

    public Task<AiSafetyAssessmentResult> AssessOutputAsync(AiSafetySubject subject, AiTextResponseInput input,
        AiTextResponseExpectation expected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSubject(subject, AiSafetyStage.AssistantOutput);
        var validated = AiTextResponsePolicy.Validate(input, expected);
        if (validated.Outcome is not (AiTextValidationOutcome.ValidText or AiTextValidationOutcome.MockText) ||
            expected.RequestFingerprint != subject.RequestFingerprint) throw new ArgumentException("Unverified AI output subject.", nameof(input));
        return AssessAsync(subject, validated.Text!, cancellationToken);
    }

    private async Task<AiSafetyAssessmentResult> AssessAsync(AiSafetySubject subject, string text, CancellationToken token)
    {
        await _privacy.RequireSameActorSatisfiedAsync(subject.Attempt.Operation.Actor, token);
        token.ThrowIfCancellationRequested();
        // Hash binds scope, message, stage, request, attempt, revision AND exact text. Not a credential.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Subject = subject, Text = text }))));
        var request = new AiSafetyAssessmentRequest(subject, text, digest);
        var result = await _gateway.AssessAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (result is null || result.Subject != subject || result.ContentDigest != digest || result.AssessmentId == Guid.Empty ||
            !Enum.IsDefined(result.Outcome) || result.Outcome == AiSafetyOutcome.Unknown) throw Unverified();
        return result;
    }

    public async Task<AiReviewAcknowledgement> SubmitUserReportAsync(AiReviewRequestBinding request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidBinding(request) || request.Origin != AiReviewOrigin.UserReport || request.Subject is not null ||
            request.TriggerId != request.RequestId) throw new ArgumentException("Invalid AI report reference.", nameof(request));
        // Reporting is not a generation request: its purpose-specific privacy policy is gateway-owned.
        await RequireActorAsync(request.Actor, cancellationToken);
        var result = await _gateway.SubmitUserReportAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); // An uncertain acceptance must be reconciled, not retried automatically.
        return ValidateAcknowledgement(result, request);
    }

    public async Task<AiReviewAcknowledgement> RouteValidatedFlagAsync(AiSafetySubject subject, Guid flagId,
        Guid requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSubject(subject);
        if (flagId == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("Invalid AI review reference.");
        await _privacy.RequireSameActorSatisfiedAsync(subject.Attempt.Operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = new AiReviewRequestBinding(subject.Attempt.Operation.Actor, subject.Attempt.Operation.ConversationId,
            subject.MessageId, requestId, AiReviewOrigin.ValidatedFlag, flagId, subject);
        var evidence = await _gateway.ValidateFlagAsync(subject, flagId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence is null) return new(binding, AiReviewAcknowledgementOutcome.PolicyUnavailable);
        if (evidence.Subject != subject || evidence.FlagId != flagId || evidence.EvidenceRevision == Guid.Empty) throw Unverified();
        if (!evidence.Validated || !evidence.SeriousRoutingPolicyVerified || !evidence.PurposeApproved ||
            !evidence.EvidenceHandlingPermissionVerified || !evidence.LifecyclePermits)
            return new(binding, AiReviewAcknowledgementOutcome.PolicyUnavailable);
        // Fresh check after validation; gateway still revalidates evidence/policy at atomic routing.
        await _privacy.RequireSameActorSatisfiedAsync(subject.Attempt.Operation.Actor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _gateway.RouteValidatedFlagAsync(binding, evidence, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateAcknowledgement(result, binding);
    }

    private async Task RequireActorAsync(AiAuthorizedActor expected, CancellationToken token)
    {
        var actor = await _authorization.GetAuthorizedActorAsync(expected.Module, token);
        token.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidActor(actor) || actor != expected) throw new UnauthorizedAccessException("AI safety access denied.");
    }
    private static void RequireSubject(AiSafetySubject? subject, AiSafetyStage? stage = null)
    {
        if (subject is null || !AiBudgetPolicy.ValidAttempt(subject.Attempt) || subject.MessageId == Guid.Empty ||
            subject.ContentRevision == Guid.Empty || !Enum.IsDefined(subject.Stage) || subject.Stage == AiSafetyStage.Unknown ||
            (stage is not null && subject.Stage != stage) || string.IsNullOrWhiteSpace(subject.RequestFingerprint))
            throw new ArgumentException("Invalid AI safety subject.", nameof(subject));
    }
    private static bool ValidBinding(AiReviewRequestBinding? request) => request is not null &&
        AiOperationPolicy.ValidActor(request.Actor) && request.ConversationId != Guid.Empty && request.MessageId != Guid.Empty &&
        request.RequestId != Guid.Empty && request.TriggerId != Guid.Empty && Enum.IsDefined(request.Origin) && request.Origin != AiReviewOrigin.Unknown;
    private static AiReviewAcknowledgement ValidateAcknowledgement(AiReviewAcknowledgement? result, AiReviewRequestBinding expected)
    {
        if (result is null || result.Request != expected || !Enum.IsDefined(result.Outcome) || result.Outcome == AiReviewAcknowledgementOutcome.Unknown)
            throw Unverified();
        if (result.Outcome is AiReviewAcknowledgementOutcome.Accepted or AiReviewAcknowledgementOutcome.Queued)
        {
            if (result.ReceiptId is null || result.ReceiptId == Guid.Empty) throw Unverified();
        }
        else if (result.ReceiptId is not null) throw Unverified();
        return result;
    }
    private static InvalidOperationException Unverified() => new("AI safety evidence could not be verified.");
}
