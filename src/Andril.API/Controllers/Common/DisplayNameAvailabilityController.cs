using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/profiles/{module}/display-name")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DisplayNameAvailabilityController(IDisplayNameAvailabilityService availabilityService) : ControllerBase
{
    [HttpGet("availability")]
    [ProducesResponseType<DisplayNameAvailabilityResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DisplayNameAvailabilityResponse>> CheckAvailability(
        string module, [FromQuery] string? name, CancellationToken cancellationToken)
    {
        var result = await availabilityService.CheckAvailabilityAsync(module, name, cancellationToken);
        return Ok(result);
    }
}
