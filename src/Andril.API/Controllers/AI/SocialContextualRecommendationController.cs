using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Recommendations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.AI;

/// <summary>
/// Request payload for invoking a consent-controlled Social contextual recommendation.
/// Requires authoritative operation and consent references derived under server-side authorization.
/// </summary>
public sealed record SocialContextualRecommendationRequest(
    Guid ConversationId,
    Guid OperationId,
    string PurposeId,
    string PurposeVersion,
    string ScopeId,
    Guid SessionEpoch,
    Guid ModuleEpoch,
    Guid ConsentRevision,
    Guid AttemptId);

/// <summary>
/// Privacy-safe response projecting the outcome of a contextual recommendation evaluation.
/// Conceals internal suppression reasons, raw diagnostic enums, and generated content if suppressed.
/// </summary>
public sealed record SocialContextualRecommendationResponse(
    bool Delivered,
    string? Content)
{
    /// <summary>
    /// Gets a value indicating whether the recommendation was delivered.
    /// </summary>
    public bool IsDelivered => Delivered;
}

/// <summary>
/// Controller for authorized, consent-controlled Social contextual AI recommendations (CF-18).
/// Enforces trusted caller identity derivation, strict Social module isolation, explicit contextual AI consent,
/// and privacy notice compliance via <see cref="AiContextualRecommendationService"/>.
/// Excluded from automatic MVC discovery via [NonController] until persistence and provider activation is approved.
/// </summary>
/// <remarks>
/// CF-18 Contextual AI Dormant Invocation Boundaries and Authoritative Dispatch Acceptance Requirements:
/// 1. Single-Use Authoritative Dispatch: A consent start request must never authorize duplicate or redundant redispatch.
///    Continuation verification (CheckPendingAsync) validates active consent state continuation and is NOT equivalent to new dispatch authorization.
/// 2. Quota Admission & Budgeting: Authoritative quota deduction and rate limiting must execute against durable persistence prior to provider dispatch.
/// 3. Completion Fencing: Once generation finishes or suppresses, the consent attempt must atomically transition to a terminal state (Completed or Failed) in the repository.
/// 4. Replay Prevention: In-memory caches are strictly prohibited as substitutes for durable transaction coordination. Multi-round state machine safety requires database transactions.
/// 5. Dormant-Only Invocations: HTTP routing and programmatic overloads are explicitly excluded from MVC action discovery via [NonAction] and [NonController] pending authoritative dispatch acceptance.
/// </remarks>
[ApiController]
[Authorize]
[NonController]
[Route("api/v1/social/ai/recommendations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SocialContextualRecommendationController : ControllerBase
{
    private readonly IAiActorAuthorization _authorization;
    private readonly AiContextualRecommendationService _recommendationService;
    private readonly AiContextualConsentService? _consentService;

    public SocialContextualRecommendationController(
        IAiActorAuthorization authorization,
        AiContextualRecommendationService recommendationService,
        AiContextualConsentService? consentService = null)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _recommendationService = recommendationService ?? throw new ArgumentNullException(nameof(recommendationService));
        _consentService = consentService;
    }

    /// <summary>
    /// Evaluates consent continuation and delivers a contextual recommendation if authorized.
    /// Conceals internal suppression reasons and never discloses generated content upon suppression.
    /// Excluded from MVC action discovery via [NonAction] while dispatch coordination remains dormant.
    /// </summary>
    [HttpPost("")]
    [NonAction]
    public async Task<ActionResult<SocialContextualRecommendationResponse>> GetRecommendationAsync(
        [FromBody] SocialContextualRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequest(request);

        var actor = await ResolveAuthorizedActorAsync(cancellationToken);

        var operationRef = new AiOperationReference(actor, request.ConversationId, request.OperationId);
        var purpose = new AiConsentPurpose(request.PurposeId, request.PurposeVersion, request.ScopeId);
        var consentRef = new AiConsentReference(operationRef, purpose, request.SessionEpoch, request.ModuleEpoch);
        var pendingConsent = new AiConsentPendingReference(consentRef, request.ConsentRevision, request.AttemptId);

        return await ProcessRecommendationCoreAsync(actor, pendingConsent, cancellationToken);
    }

    /// <summary>
    /// Direct domain invocation overload for direct security testing and in-memory authorization pipelines.
    /// Excluded from MVC action discovery via [NonAction].
    /// Verifies trusted caller identity, foreign actor rejection, and module isolation.
    /// </summary>
    [NonAction]
    public async Task<ActionResult<SocialContextualRecommendationResponse>> GetRecommendationAsync(
        AiConsentPendingReference pendingConsent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(pendingConsent);

        var actor = await ResolveAuthorizedActorAsync(cancellationToken);

        // Foreign actor reference rejection
        if (pendingConsent.Reference.Operation.Actor != actor)
        {
            throw new UnauthorizedAccessException("Foreign actor reference is not authorized.");
        }

        // Cross-module recommendation rejection
        if (pendingConsent.Reference.Operation.Actor.Module != AiModule.Social)
        {
            throw new UnauthorizedAccessException("Cross-module AI recommendations are prohibited.");
        }

        return await ProcessRecommendationCoreAsync(actor, pendingConsent, cancellationToken);
    }

    private async Task<ActionResult<SocialContextualRecommendationResponse>> ProcessRecommendationCoreAsync(
        AiAuthorizedActor actor,
        AiConsentPendingReference pendingConsent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _recommendationService.ProcessRecommendationAsync(
            pendingConsent,
            AiModule.Social,
            cancellationToken);

        // Recheck caller identity has not shifted during asynchronous execution
        var recheckActor = await ResolveAuthorizedActorAsync(cancellationToken);
        if (recheckActor != actor)
        {
            throw new UnauthorizedAccessException("AI authorization context changed during processing.");
        }

        // Suppressed recommendations must NEVER disclose generated content.
        // Conceal internal diagnostics and suppression reasons.
        if (result.IsDelivered && !string.IsNullOrWhiteSpace(result.RecommendationContent))
        {
            return Ok(new SocialContextualRecommendationResponse(Delivered: true, Content: result.RecommendationContent));
        }

        return Ok(new SocialContextualRecommendationResponse(Delivered: false, Content: null));
    }

    private async Task<AiAuthorizedActor> ResolveAuthorizedActorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var actor = await _authorization.GetAuthorizedActorAsync(AiModule.Social, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null || !AiOperationPolicy.ValidActor(actor) || actor.Module != AiModule.Social)
        {
            throw new UnauthorizedAccessException("Social AI module authorization is unavailable.");
        }

        return actor;
    }

    private static void ValidateRequest(SocialContextualRecommendationRequest request)
    {
        if (request.ConversationId == Guid.Empty)
            throw new ArgumentException("A valid ConversationId is required.", nameof(request));

        if (request.OperationId == Guid.Empty)
            throw new ArgumentException("A valid OperationId is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.PurposeId))
            throw new ArgumentException("A valid PurposeId is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.PurposeVersion))
            throw new ArgumentException("A valid PurposeVersion is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.ScopeId))
            throw new ArgumentException("A valid ScopeId is required.", nameof(request));

        if (request.SessionEpoch == Guid.Empty)
            throw new ArgumentException("A valid SessionEpoch is required.", nameof(request));

        if (request.ModuleEpoch == Guid.Empty)
            throw new ArgumentException("A valid ModuleEpoch is required.", nameof(request));

        if (request.ConsentRevision == Guid.Empty)
            throw new ArgumentException("A valid ConsentRevision is required.", nameof(request));

        if (request.AttemptId == Guid.Empty)
            throw new ArgumentException("A valid AttemptId is required.", nameof(request));
    }
}
