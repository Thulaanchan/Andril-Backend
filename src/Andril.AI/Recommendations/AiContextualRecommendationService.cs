using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;

namespace Andril.AI.Recommendations;

/// <summary>
/// Status outcome of contextual recommendation processing.
/// </summary>
public enum AiContextualRecommendationDeliveryStatus
{
    /// <summary>Recommendation was successfully generated and delivered under active consent.</summary>
    Delivered,

    /// <summary>Recommendation delivery was suppressed due to consent state, revocation, or policy constraints.</summary>
    Suppressed
}

/// <summary>
/// Classified reason for suppressing recommendation delivery.
/// </summary>
public enum AiContextualSuppressionReason
{
    /// <summary>Consent continuation could not be verified.</summary>
    ConsentNotVerified,

    /// <summary>Consent was revoked, withdrawn, or expired.</summary>
    ConsentWithdrawn,

    /// <summary>Consent snapshot is in an invalid or non-started state.</summary>
    ConsentStateInvalid,

    /// <summary>Privacy notice requirement was not satisfied.</summary>
    PrivacyNoticeNotSatisfied,

    /// <summary>Target module does not match consent scope.</summary>
    ModuleMismatch
}

/// <summary>
/// Immutable, privacy-preserving outcome of a consent-controlled contextual recommendation delivery.
/// </summary>
public sealed class AiContextualRecommendationDeliveryResult
{
    /// <summary>Gets the delivery status.</summary>
    [JsonIgnore]
    public AiContextualRecommendationDeliveryStatus Status { get; }

    /// <summary>Gets a value indicating whether the recommendation was delivered.</summary>
    [JsonIgnore]
    public bool IsDelivered => Status == AiContextualRecommendationDeliveryStatus.Delivered;

    /// <summary>Gets a value indicating whether delivery was suppressed.</summary>
    [JsonIgnore]
    public bool IsSuppressed => Status == AiContextualRecommendationDeliveryStatus.Suppressed;

    /// <summary>Gets the suppression reason, if suppressed.</summary>
    [JsonIgnore]
    public AiContextualSuppressionReason? SuppressionReason { get; }

    /// <summary>Gets the generated recommendation content, if delivered.</summary>
    [JsonIgnore]
    public string? RecommendationContent { get; }

    private AiContextualRecommendationDeliveryResult(
        AiContextualRecommendationDeliveryStatus status,
        AiContextualSuppressionReason? suppressionReason,
        string? recommendationContent)
    {
        Status = status;
        SuppressionReason = suppressionReason;
        RecommendationContent = recommendationContent;
    }

    public static AiContextualRecommendationDeliveryResult Delivered(string content) =>
        new(AiContextualRecommendationDeliveryStatus.Delivered, null, content ?? throw new ArgumentNullException(nameof(content)));

    public static AiContextualRecommendationDeliveryResult Suppressed(AiContextualSuppressionReason reason) =>
        new(AiContextualRecommendationDeliveryStatus.Suppressed, reason, null);

    /// <inheritdoc />
    public override string ToString() => "AiContextualRecommendationDeliveryResult [private]";
}

/// <summary>
/// Generator contract for producing contextual recommendations under approved consent.
/// </summary>
public interface IAiContextualRecommendationGenerator
{
    /// <summary>
    /// Generates recommendation content using minimal authorized context for the approved purpose.
    /// </summary>
    Task<string> GenerateRecommendationAsync(
        AiModule module,
        AiAuthorizedActor actor,
        AiConsentPurpose purpose,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service orchestrating consent-controlled contextual AI recommendation delivery.
/// Enforces explicit consent continuation, privacy notice compliance, minimal context scoping,
/// cross-module isolation, and automatic delivery suppression upon consent withdrawal.
/// </summary>
public sealed class AiContextualRecommendationService
{
    private readonly IAiActorAuthorization _authorization;
    private readonly AiPrivacyNoticeGuard _privacyGuard;
    private readonly AiContextualConsentService _consentService;
    private readonly IAiContextualRecommendationGenerator _generator;

    public AiContextualRecommendationService(
        IAiActorAuthorization authorization,
        AiPrivacyNoticeGuard privacyGuard,
        AiContextualConsentService consentService,
        IAiContextualRecommendationGenerator generator)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _privacyGuard = privacyGuard ?? throw new ArgumentNullException(nameof(privacyGuard));
        _consentService = consentService ?? throw new ArgumentNullException(nameof(consentService));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    /// <summary>
    /// Evaluates consent continuation and generates a contextual recommendation if authorized,
    /// or suppresses delivery if consent is withdrawn, expired, or mismatched.
    /// </summary>
    public async Task<AiContextualRecommendationDeliveryResult> ProcessRecommendationAsync(
        AiConsentPendingReference pendingConsent,
        AiModule targetModule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (pendingConsent is null)
        {
            throw new ArgumentNullException(nameof(pendingConsent));
        }

        // 1. Cross-module rejection: consent operation module must match target module
        if (pendingConsent.Reference.Operation.Actor.Module != targetModule)
        {
            throw new UnauthorizedAccessException("Cross-module AI contextual recommendation processing is strictly rejected.");
        }

        // 2. Resolve and verify current authorized actor
        var currentActor = await _authorization.GetAuthorizedActorAsync(targetModule, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (currentActor is null || currentActor != pendingConsent.Reference.Operation.Actor)
        {
            throw new UnauthorizedAccessException("Current authorized actor does not match the consent operation actor.");
        }

        // 3. Privacy notice guard check
        await _privacyGuard.RequireSameActorSatisfiedAsync(currentActor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // 4. Check explicit contextual consent continuation
        var consentResult = await _consentService.CheckPendingAsync(pendingConsent, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (consentResult.Outcome != AiConsentOutcome.ContinuationVerified)
        {
            return AiContextualRecommendationDeliveryResult.Suppressed(AiContextualSuppressionReason.ConsentNotVerified);
        }

        if (consentResult.Snapshot is null || consentResult.Snapshot.State != AiConsentState.Started)
        {
            return AiContextualRecommendationDeliveryResult.Suppressed(AiContextualSuppressionReason.ConsentStateInvalid);
        }

        // 5. Generate minimal contextual recommendation
        var content = await _generator.GenerateRecommendationAsync(
            targetModule, currentActor, pendingConsent.Reference.Purpose, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return AiContextualRecommendationDeliveryResult.Delivered(content);
    }
}
