using Andril.API.Authorization.Social;
using Andril.Social.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

[ApiController]
[Authorize]
[Route("api/v1/social/questionnaires")]
public sealed class SocialQuestionnaireDefinitionsController(
    ISocialQuestionnaireDefinitionAccessService accessService) : ControllerBase
{
    [HttpGet("active")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<QuestionnaireDefinitionDto>> GetActiveDefinition(CancellationToken cancellationToken)
    {
        var definition = await accessService.GetActiveDefinitionAsync(cancellationToken);
        return Ok(definition);
    }
}
