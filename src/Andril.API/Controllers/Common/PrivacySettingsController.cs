using Andril.Common.Accounts.Privacy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[NonController]
[ApiController]
[Authorize]
[Route("api/v1/account/privacy")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PrivacySettingsController(IUserPrivacySettingsService privacyService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UserPrivacySettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserPrivacySettingsDto>> Get(CancellationToken cancellationToken)
    {
        var settings = await privacyService.GetMyPrivacySettingsAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPut]
    [ProducesResponseType<UserPrivacySettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserPrivacySettingsDto>> Put([FromBody] UpdatePrivacySettingsRequest request, CancellationToken cancellationToken)
    {
        var updated = await privacyService.UpdateMyPrivacySettingsAsync(request, cancellationToken);
        return Ok(updated);
    }
}
