using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Contracts.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

/// <summary>
/// Privacy-safe DTO for an individual questionnaire answer item in an active response.
/// Conceals private profile identifiers and internal entity keys.
/// </summary>
public sealed record SocialQuestionnaireAnswerItemDto(
    string QuestionId,
    string OptionId);

/// <summary>
/// Privacy-safe DTO projecting the caller's active Social questionnaire response.
/// Conceals private profile IDs, unauthorized answers, and internal database keys.
/// </summary>
public sealed record SocialQuestionnaireActiveResponseDto(
    Guid ResponseId,
    string Version,
    string Status,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SocialQuestionnaireAnswerItemDto> Answers);

/// <summary>
/// Privacy-safe DTO returned upon successful questionnaire submission.
/// </summary>
public sealed record SocialQuestionnaireSubmissionResponseDto(
    Guid ResponseId,
    string Status);

/// <summary>
/// Controller for authorized Social questionnaire response submission and owner-scoped retrieval (SOC-01).
/// Enforces trusted server-side authorization, strict payload validation, privacy-safe projections,
/// and no-store caching headers.
/// Excluded from automatic MVC discovery via [NonController] until persistence activation is approved.
/// </summary>
[ApiController]
[Authorize]
[NonController]
[Route("api/v1/social/questionnaires/responses")]
public sealed class SocialQuestionnaireResponsesController : ControllerBase
{
    private readonly ISocialQuestionnairePersistentSubmissionOrchestrator _orchestrator;
    private readonly ISocialQuestionnaireLifecycleAccessService _lifecycleAccessService;
    private readonly SocialQuestionnaireSubmissionBodyParser _bodyParser;

    public SocialQuestionnaireResponsesController(
        ISocialQuestionnairePersistentSubmissionOrchestrator orchestrator,
        ISocialQuestionnaireLifecycleAccessService lifecycleAccessService,
        SocialQuestionnaireSubmissionBodyParser? bodyParser = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _lifecycleAccessService = lifecycleAccessService ?? throw new ArgumentNullException(nameof(lifecycleAccessService));
        _bodyParser = bodyParser ?? new SocialQuestionnaireSubmissionBodyParser();
    }

    /// <summary>
    /// Submits a new Social questionnaire response snapshot.
    /// Uses strict payload parsing, validates domain rules, and atomically persists the response.
    /// </summary>
    [HttpPost("")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SocialQuestionnaireSubmissionResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SocialQuestionnaireSubmissionResponseDto>> SubmitResponse(
        [FromBody] JsonElement rawPayload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (rawPayload.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid submission payload.",
                Detail = "Root payload must be a JSON object."
            });
        }

        var json = rawPayload.GetRawText();
        var parseResult = _bodyParser.Parse(json);
        if (!parseResult.IsSuccess || parseResult.Submission is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid submission payload.",
                Detail = parseResult.Error ?? "Failed to parse questionnaire submission."
            });
        }

        return await ExecuteSubmissionAsync(parseResult.Submission, cancellationToken);
    }

    /// <summary>
    /// Direct submission action for strongly typed programmatic invocation.
    /// Excluded from HTTP routing.
    /// </summary>
    [NonAction]
    public async Task<ActionResult<SocialQuestionnaireSubmissionResponseDto>> SubmitResponse(
        QuestionnaireSubmission submission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(submission);

        return await ExecuteSubmissionAsync(submission, cancellationToken);
    }

    /// <summary>
    /// Retrieves the active questionnaire response belonging to the caller's authorized Social profile scope.
    /// Conceals internal profile keys and private diagnostic details.
    /// </summary>
    [HttpGet("me")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SocialQuestionnaireActiveResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SocialQuestionnaireActiveResponseDto>> GetMyActiveResponse(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var activeResponse = await _lifecycleAccessService.GetActiveResponseAsync(cancellationToken);
            if (activeResponse is null)
            {
                return NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "No active questionnaire response found.",
                    Detail = "No active questionnaire response exists for the caller's authorized Social profile."
                });
            }

            var answerDtos = activeResponse.Answers
                .Select(a => new SocialQuestionnaireAnswerItemDto(a.QuestionId, a.OptionId))
                .ToArray();

            var projection = new SocialQuestionnaireActiveResponseDto(
                activeResponse.Id,
                activeResponse.Version,
                activeResponse.Status.ToString(),
                activeResponse.CreatedAtUtc,
                answerDtos);

            return Ok(projection);
        }
        catch (ProfileAccessDeniedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Access denied to Social questionnaire responses.",
                Detail = "Caller does not possess authorized Social profile access."
            });
        }
        catch (ProfileMissingException)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Social profile not found.",
                Detail = "No authorized Social profile was found for the caller."
            });
        }
    }

    private async Task<ActionResult<SocialQuestionnaireSubmissionResponseDto>> ExecuteSubmissionAsync(
        QuestionnaireSubmission submission,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _orchestrator.SubmitAndPersistAsync(submission, cancellationToken);
            if (!result.IsSuccess)
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Questionnaire submission rejected.",
                    Detail = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : result.DiagnosticMessage
                });
            }

            return Ok(new SocialQuestionnaireSubmissionResponseDto(
                result.ResponseId!.Value,
                result.Status.ToString()));
        }
        catch (ProfileAccessDeniedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Access denied to Social questionnaire responses.",
                Detail = "Caller does not possess authorized Social profile access."
            });
        }
        catch (ProfileMissingException)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Social profile not found.",
                Detail = "No authorized Social profile was found for the caller."
            });
        }
    }
}
