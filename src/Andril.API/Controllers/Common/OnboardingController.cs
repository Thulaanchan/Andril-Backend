using Andril.Common.Accounts.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/onboarding")]
public sealed class OnboardingController(IOnboardingStatusService onboarding) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<OnboardingStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await onboarding.GetAsync(cancellationToken));
}
