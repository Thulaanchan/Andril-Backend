using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Microsoft.EntityFrameworkCore;

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
/// Explicit opt-in activation adds this controller after verified persistence registration.
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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
            var detail = SanitizeParseError(parseResult.Error);
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid submission payload.",
                Detail = detail
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
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
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

            if (!Enum.IsDefined(activeResponse.Status))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Invalid questionnaire response state.",
                    Detail = "Stored questionnaire response contains an invalid participation status."
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
        catch (Exception error) when (IsStorageFailure(error))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Questionnaire storage is unavailable."
            });
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

    private static bool IsStorageFailure(Exception error) => error is DbException or DbUpdateException or TimeoutException
        or InvalidOperationException { InnerException: DbException or TimeoutException };

    private static readonly HashSet<string> SafeValidationCodes = new(StringComparer.Ordinal)
    {
        "INVALID_SUBMISSION",
        "VERSION_MISMATCH",
        "SKIP_WITH_ANSWERS",
        "INVALID_ANSWER",
        "DUPLICATE_ANSWER",
        "UNKNOWN_QUESTION",
        "INVALID_OPTION",
        "INCOMPLETE_ANSWERS",
        "NOT_FOUND"
    };

    private static readonly string[] SensitiveKeywords =
    [
        "select ", "insert ", "update ", "delete ", "drop ", "where ", "from ",
        "table ", "database", "sql", "npgsql", "postgres",
        "exception", "stack trace", "stacktrace", "system.", "andril.",
        "secret", "password", "token", "connection", "profileid", "credential"
    ];

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ExactAllowedParseErrors = new(StringComparer.Ordinal)
    {
        "Payload cannot be null or empty.",
        "Unexpected end of JSON payload.",
        "Root payload must be a JSON object.",
        "Expected JSON property name.",
        "Property name cannot be null or empty.",
        "Property 'version' must be a string.",
        "Property 'version' must be a non-empty string within valid length bounds.",
        "Property 'skip' must be a boolean value.",
        "Property 'answers' must be an array.",
        "Each element in 'answers' must be a JSON object.",
        "Expected property name in answer object.",
        "Property name in answer object cannot be empty.",
        "'questionId' must be a string.",
        "'questionId' must be a non-empty string within valid bounds.",
        "'optionId' must be a string.",
        "'optionId' must be a non-empty string within valid bounds.",
        "Answer object is missing required 'questionId'.",
        "Answer object is missing required 'optionId'.",
        "Unexpected trailing content after JSON root object.",
        "Missing required property 'version'."
    };

    private static string SanitizeParseError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || ContainsSensitiveDiagnostics(error))
        {
            return "Failed to parse questionnaire submission.";
        }

        if (error.StartsWith("Malformed JSON syntax", StringComparison.OrdinalIgnoreCase) ||
            error.StartsWith("Invalid payload structure", StringComparison.OrdinalIgnoreCase))
        {
            return "Malformed JSON payload syntax.";
        }

        // Never echo unknown, disallowed, or duplicate property names to prevent diagnostic or property probing
        if (error.StartsWith("Unknown property", StringComparison.OrdinalIgnoreCase))
        {
            return error.Contains("answer object", StringComparison.OrdinalIgnoreCase)
                ? "Unknown property in answer object is not permitted."
                : "Unknown property in submission payload is not permitted.";
        }

        if (error.StartsWith("Disallowed property", StringComparison.OrdinalIgnoreCase))
        {
            return error.Contains("answer object", StringComparison.OrdinalIgnoreCase)
                ? "Disallowed property in answer object detected."
                : "Disallowed property detected in submission payload.";
        }

        if (error.StartsWith("Duplicate property", StringComparison.OrdinalIgnoreCase))
        {
            return error.Contains("answer object", StringComparison.OrdinalIgnoreCase)
                ? "Duplicate property detected in answer object."
                : "Duplicate property detected in root object.";
        }

        if (error.StartsWith("Payload size", StringComparison.OrdinalIgnoreCase))
        {
            return "Payload size exceeds maximum allowable limit.";
        }

        if (ExactAllowedParseErrors.Contains(error))
        {
            return error;
        }

        return "Failed to parse questionnaire submission.";
    }

    private static readonly IReadOnlyDictionary<string, string> RequiredAnswerMessages =
        QuestionnaireV1Definition.Create().Questions
            .Where(question => question.IsRequired)
            .ToDictionary(
                question => $"Incomplete answers: question '{question.Id}' is required.",
                question => $"Incomplete answers: question '{question.Id}' is required.",
                StringComparer.Ordinal);

    private static string? SanitizeValidationError(string error)
    {
        if (string.IsNullOrWhiteSpace(error) || ContainsSensitiveDiagnostics(error))
        {
            return null;
        }

        if (SafeValidationCodes.TryGetValue(error, out var code))
        {
            return code;
        }

        // Exact canonical membership rejects unknown IDs, suffixes and multiline diagnostics.
        // Return server-constructed feedback rather than forwarding the diagnostic input.
        return RequiredAnswerMessages.TryGetValue(error, out var message) ? message : null;
    }

    private static bool ContainsSensitiveDiagnostics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        foreach (var keyword in SensitiveKeywords)
        {
            if (lower.Contains(keyword))
            {
                return true;
            }
        }

        if (GuidPattern.IsMatch(text))
        {
            return true;
        }

        return false;
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
                if (result.Errors.Count == 1 && result.Errors[0] == SocialQuestionnairePersistentSubmissionResult.CurrentResponseExists)
                {
                    return Conflict(new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "A current questionnaire response already exists.",
                        Extensions = { ["code"] = SocialQuestionnairePersistentSubmissionResult.CurrentResponseExists }
                    });
                }

                var safeErrors = result.Errors
                    .Select(SanitizeValidationError)
                    .Where(error => error is not null)
                    .ToArray();

                var detail = safeErrors.Length > 0
                    ? string.Join("; ", safeErrors)
                    : "Questionnaire submission failed validation.";

                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Questionnaire submission rejected.",
                    Detail = detail
                });
            }

            if (!result.ResponseId.HasValue ||
                result.ResponseId.Value == Guid.Empty ||
                !Enum.IsDefined(result.Status) ||
                result.Status == QuestionnaireParticipationStatus.NotStarted)
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Questionnaire submission rejected.",
                    Detail = "Questionnaire submission outcome was invalid or contradictory."
                });
            }

            return Ok(new SocialQuestionnaireSubmissionResponseDto(
                result.ResponseId.Value,
                result.Status.ToString()));
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Questionnaire storage is unavailable."
            });
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
