using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[NonController]
[ApiController]
[Authorize]
[Route("api/v1/account/welcome/{module}")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ModuleWelcomeController(IModuleWelcomeService welcomeService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ModuleWelcomeStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ModuleWelcomeStatusDto>> GetStatus(
        string module, CancellationToken cancellationToken)
    {
        var status = await welcomeService.GetWelcomeStatusAsync(module, cancellationToken);
        return Ok(status);
    }

    [HttpPost("get-started")]
    [ProducesResponseType<ModuleWelcomeGetStartedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ModuleWelcomeGetStartedResponse>> GetStarted(
        string module,
        [FromBody] ModuleWelcomeGetStartedRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await welcomeService.GetStartedAsync(module, request, cancellationToken);
        return Ok(response);
    }
}
