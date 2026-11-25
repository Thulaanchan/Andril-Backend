using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

/// <summary>
/// Privacy-safe DTO projecting an eligible, ranked Social candidate card for client consumption.
/// Conceals caller identity, private questionnaire answers, precise locations, and internal scoring diagnostics.
/// </summary>
public sealed record SocialExploreCandidateDto(
    Guid CandidateProfileId,
    string DisplayName,
    int Age,
    int? ApproximateDistanceKm,
    IReadOnlyList<string> SharedLanguages,
    IReadOnlyList<string> SharedConnectionGoals);

/// <summary>
/// Controller for authorized, privacy-safe Social ranked discovery and explore feed (SOC-02).
/// Delegates candidate discovery to <see cref="ISocialDiscoveryAccessService"/> under trusted caller scope.
/// Excluded from automatic MVC discovery via [NonController] until persistence activation is approved.
/// </summary>
[ApiController]
[Authorize]
[NonController]
[Route("api/v1/social/explore")]
public sealed class SocialExploreController : ControllerBase
{
    private readonly ISocialDiscoveryAccessService _discoveryAccessService;

    public SocialExploreController(ISocialDiscoveryAccessService discoveryAccessService)
    {
        _discoveryAccessService = discoveryAccessService ?? throw new ArgumentNullException(nameof(discoveryAccessService));
    }

    /// <summary>
    /// Retrieves a privacy-safe, ranked candidate explore feed for the authenticated caller.
    /// Supports deterministic rotation offset and bounded pagination.
    /// </summary>
    [HttpGet("")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<IReadOnlyList<SocialExploreCandidateDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SocialExploreCandidateDto>>> GetExploreFeed(
        [FromQuery] int rotationOffset = 0,
        [FromQuery] int limit = SocialDiscoveryAccessService.DefaultCandidateLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (rotationOffset < 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid rotation offset.",
                Detail = "Rotation offset cannot be negative."
            });
        }

        if (limit <= 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid candidate limit.",
                Detail = "Candidate limit must be greater than zero."
            });
        }

        try
        {
            var cards = await _discoveryAccessService.DiscoverCandidatesAsync(
                rotationOffset,
                limit,
                cancellationToken);

            var dtos = cards.Select(c => new SocialExploreCandidateDto(
                CandidateProfileId: c.CandidateProfileId,
                DisplayName: c.DisplayName,
                Age: c.Age,
                ApproximateDistanceKm: c.ApproximateDistanceKm,
                SharedLanguages: c.SharedLanguages,
                SharedConnectionGoals: c.SharedConnectionGoals)).ToArray();

            return Ok(dtos);
        }
        catch (ProfileAccessDeniedException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Profile access denied.",
                Detail = ex.Message
            });
        }
    }
}
