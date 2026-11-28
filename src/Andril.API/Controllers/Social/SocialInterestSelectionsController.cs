using System.Text.Json.Serialization;
using Andril.Persistence.Repositories.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceSocialInterestsRequest([property: JsonRequired] Guid[] InterestIds);

[ApiController, Authorize, NonController, SocialMatchingErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(403)]
[ProducesResponseType<ProblemDetails>(503)]
[Route("api/v1/social/matching/interests")]
public sealed class SocialInterestSelectionsController(PostgresSocialInterestRepository interests) : ControllerBase
{
    [HttpGet("catalogue")]
    [ProducesResponseType<List<SocialInterestCatalogueItem>>(200)]
    public Task<List<SocialInterestCatalogueItem>> ReadCatalogue(CancellationToken token) => interests.ReadCatalogueAsync(token);

    [HttpGet("me")]
    [ProducesResponseType<SocialInterestSelection>(200)]
    public Task<SocialInterestSelection> ReadSelf(CancellationToken token) => interests.ReadSelfAsync(token);

    [HttpPut("me"), StrictSocialJson]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Replace([FromBody] ReplaceSocialInterestsRequest request, CancellationToken token)
    {
        await interests.ReplaceAsync(request.InterestIds, token);
        return NoContent();
    }
}
