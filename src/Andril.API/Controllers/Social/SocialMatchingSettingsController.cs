using System.Text.Json.Serialization;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Social;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SocialMatchingPreferenceRequest(
    [property: JsonRequired] bool Discoverable,
    [property: JsonRequired] string ShowMe,
    [property: JsonRequired] int MinAge,
    [property: JsonRequired] int MaxAge,
    [property: JsonRequired] int MinDistanceKm,
    [property: JsonRequired] int MaxDistanceKm,
    [property: JsonRequired] string[] Languages,
    [property: JsonRequired] string[] ConnectionGoals);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SocialRestrictionRequest([property: JsonRequired] bool Restricted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SocialLocationUpdateRequest([property: JsonRequired] decimal Latitude,
    [property: JsonRequired] decimal Longitude, [property: JsonRequired] bool Consent);

[ApiController, Authorize, NonController, SocialMatchingErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[Route("api/v1/social")]
public sealed class SocialMatchingSettingsController(PostgresSocialProfileEvidenceRepository profiles, PostgresSocialSafetyRepository safety)
    : ControllerBase
{
    [HttpPut("matching/preferences"), StrictSocialJson]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutPreferences([FromBody] SocialMatchingPreferenceRequest input, CancellationToken token)
    {
        var ranges = new SocialExploreRangeFilters(input.MinAge, input.MaxAge, input.MinDistanceKm, input.MaxDistanceKm);
        await profiles.SetSelfPreferencesAsync(new SocialMatchingPreferences(input.Discoverable, ranges, input.ShowMe,
            input.Languages, input.ConnectionGoals), token, preservePartnerPreferences: true);
        return NoContent();
    }

    [HttpPut("safety/blocks/{targetProfileId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Block(Guid targetProfileId, CancellationToken token)
    {
        await safety.SetBlockAsync(targetProfileId, true, token);
        return NoContent();
    }

    [HttpDelete("safety/blocks/{targetProfileId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unblock(Guid targetProfileId, CancellationToken token)
    {
        await safety.SetBlockAsync(targetProfileId, false, token);
        return NoContent();
    }

    [HttpPut("safety/restrictions/{targetProfileId:guid}"), StrictSocialJson]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetRestriction(Guid targetProfileId, [FromBody] SocialRestrictionRequest input, CancellationToken token)
    {
        await safety.SetRestrictionAsync(targetProfileId, input.Restricted, token);
        return NoContent();
    }
}

[ApiController, Authorize, NonController, SocialMatchingErrors]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[Route("api/v1/social/location")]
public sealed class SocialLocationController(PostgresSocialLocationRepository locations) : ControllerBase
{
    [HttpPut, StrictSocialJson]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update([FromBody] SocialLocationUpdateRequest input, CancellationToken token)
    {
        await locations.UpdateSelfAsync(input.Latitude, input.Longitude, input.Consent, token);
        return NoContent();
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Withdraw(CancellationToken token)
    {
        await locations.WithdrawSelfAsync(token);
        return NoContent();
    }
}
