using System.Text.Json.Serialization;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Personality;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SocialPartnerPreferenceUpdate([property: JsonRequired] SocialEngagementPreference? Social,
    [property: JsonRequired] CommunicationPreference? Communication, [property: JsonRequired] PlanningPreference? Planning);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SocialArchetypeConfirmationRequest([property: JsonRequired] Guid QuestionnaireResponseId,
    SocialEngagementTendency? Social, CommunicationTendency? Communication, PlanningTendency? Planning);

[ApiController, Authorize, NonController, SocialMatchingErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(403)]
[ProducesResponseType<ProblemDetails>(503)]
[Route("api/v1/social/matching/personality")]
public sealed class SocialPersonalityController(PostgresSocialPersonalityRepository personality) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<SocialPersonalityView>(200)]
    public Task<SocialPersonalityView> Read(CancellationToken token) => personality.ReadAsync(token);

    [HttpPut("preferences"), StrictSocialJson]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Preferences([FromBody] SocialPartnerPreferenceUpdate request, CancellationToken token)
    {
        PersonalityPartnerPreferences? preferences;
        if (request.Social is { } social && request.Communication is { } communication && request.Planning is { } planning)
            preferences = new(social, communication, planning);
        else if (request.Social is null && request.Communication is null && request.Planning is null)
            preferences = null;
        else
            throw new ArgumentException("Supply all Social partner preferences or clear them together.");
        await personality.SetPartnerAsync(preferences, token);
        return NoContent();
    }

    [HttpPut("confirmation"), StrictSocialJson]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Confirm([FromBody] SocialArchetypeConfirmationRequest request, CancellationToken token)
    {
        await personality.ConfirmAsync(request.QuestionnaireResponseId, request.Social, request.Communication, request.Planning, token);
        return NoContent();
    }
}
