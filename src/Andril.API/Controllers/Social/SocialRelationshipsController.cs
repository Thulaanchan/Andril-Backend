using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

/// <summary>
/// Request payload for recording caller directional dating interest toward a target candidate profile.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SocialRecordInterestRequest(
    [property: JsonRequired] Guid TargetProfileId,
    [property: JsonRequired] bool IsPositive);

/// <summary>
/// Privacy-safe response projected from a directional interest operation.
/// Conceals unilateral interest and internal diagnostics; reveals mutual match info only if Matched.
/// </summary>
public sealed record SocialRelationshipOperationResponseDto(
    bool Success,
    bool MatchCreated,
    Guid? MatchId,
    string VisibleState);

/// <summary>
/// Privacy-safe response projecting relationship standing visible to the authenticated caller.
/// Conceals unreciprocated directional decisions; reveals match details only if an active mutual match exists.
/// </summary>
public sealed record SocialRelationshipStatusResponseDto(
    bool HasActiveMatch,
    Guid? MatchId,
    DateTime? MatchedAtUtc);

/// <summary>
/// Response projecting the outcome of a match revocation (unmatch) operation.
/// </summary>
public sealed record SocialRevocationResponseDto(
    bool Success,
    bool MatchRevoked);

/// <summary>
/// Controller for authorized Social dating relationship operations: interest recording,
/// privacy-preserving status inspection, and mutual-match revocation (CF-20).
/// Delegates to <see cref="ISocialRelationshipAccessService"/> under trusted caller authorization.
/// Excluded from automatic MVC discovery via [NonController] until persistence activation is approved.
/// </summary>
[ApiController]
[Authorize]
[NonController]
[SocialMatchingErrors]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[Route("api/v1/social/relationships")]
public sealed class SocialRelationshipsController : ControllerBase
{
    private readonly ISocialRelationshipAccessService _relationshipAccessService;

    public SocialRelationshipsController(ISocialRelationshipAccessService relationshipAccessService)
    {
        _relationshipAccessService = relationshipAccessService ?? throw new ArgumentNullException(nameof(relationshipAccessService));
    }

    /// <summary>
    /// Records the authenticated caller's directional interest toward a target candidate.
    /// Conditionally establishes an atomic mutual match when reciprocal positive interest exists.
    /// Never reveals unreciprocated decisions.
    /// </summary>
    [HttpPost("interest"), StrictSocialJson]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SocialRelationshipOperationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SocialRelationshipOperationResponseDto>> RecordInterest(
        [FromBody] SocialRecordInterestRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request is null || request.TargetProfileId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid interest request.",
                Detail = "Target profile identity must be a non-empty GUID."
            });
        }

        try
        {
            var result = await _relationshipAccessService.RecordInterestAsync(
                request.TargetProfileId,
                request.IsPositive,
                cancellationToken);

            var dto = new SocialRelationshipOperationResponseDto(
                Success: result.Success,
                MatchCreated: result.MatchCreated,
                MatchId: result.MatchId,
                VisibleState: result.VisibleState.ToString());

            return Ok(dto);
        }
        catch (ProfileAccessDeniedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Profile access denied.",
                Detail = "Caller is not authorized to perform the requested relationship action."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid interest operation.",
                Detail = "The relationship interest operation could not be completed with the provided parameters."
            });
        }
        catch (InvalidOperationException error) when (!SocialMatchingErrorsAttribute.IsDatabaseUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid interest operation.",
                Detail = "The relationship interest operation could not be completed due to a conflict or invalid state."
            });
        }
    }

    /// <summary>
    /// Retrieves the privacy-preserving relationship status between the authenticated caller and a target candidate.
    /// Conceals unreciprocated directional interest and candidate rejection.
    /// </summary>
    [HttpGet("{targetProfileId:guid}/status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SocialRelationshipStatusResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SocialRelationshipStatusResponseDto>> GetRelationshipStatus(
        [FromRoute] Guid targetProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (targetProfileId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid target profile identity.",
                Detail = "Target profile identity must be a non-empty GUID."
            });
        }

        try
        {
            var status = await _relationshipAccessService.GetRelationshipStatusAsync(
                targetProfileId,
                cancellationToken);

            var dto = new SocialRelationshipStatusResponseDto(
                HasActiveMatch: status.HasActiveMatch,
                MatchId: status.MatchId,
                MatchedAtUtc: status.MatchedAtUtc);

            return Ok(dto);
        }
        catch (ProfileAccessDeniedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Profile access denied.",
                Detail = "Caller is not authorized to access relationship status."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid status operation.",
                Detail = "The relationship status operation could not be completed with the provided parameters."
            });
        }
        catch (InvalidOperationException error) when (!SocialMatchingErrorsAttribute.IsDatabaseUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid status operation.",
                Detail = "The relationship status operation could not be completed due to a conflict or invalid state."
            });
        }
    }

    /// <summary>
    /// Revokes an existing mutual match between the authenticated caller and a target candidate.
    /// </summary>
    [HttpPost("{targetProfileId:guid}/revoke")]
    [HttpDelete("{targetProfileId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SocialRevocationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SocialRevocationResponseDto>> RevokeMatch(
        [FromRoute] Guid targetProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (targetProfileId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid target profile identity.",
                Detail = "Target profile identity must be a non-empty GUID."
            });
        }

        try
        {
            var result = await _relationshipAccessService.RevokeMatchAsync(
                targetProfileId,
                cancellationToken);

            var dto = new SocialRevocationResponseDto(
                Success: result.Success,
                MatchRevoked: result.MatchRevoked);

            return Ok(dto);
        }
        catch (ProfileAccessDeniedException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Profile access denied.",
                Detail = "Caller is not authorized to revoke the specified relationship."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid revocation operation.",
                Detail = "The relationship revocation operation could not be completed with the provided parameters."
            });
        }
        catch (InvalidOperationException error) when (!SocialMatchingErrorsAttribute.IsDatabaseUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid revocation operation.",
                Detail = "The relationship revocation operation could not be completed due to a conflict or invalid state."
            });
        }
    }
}
