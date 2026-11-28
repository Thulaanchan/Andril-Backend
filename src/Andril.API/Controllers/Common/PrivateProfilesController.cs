using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/profiles/{module}")]
public sealed class PrivateProfilesController(IPrivateProfileService profiles) : ControllerBase
{
    [NonAction]
    [HttpGet("setup-status")]
    [ProducesResponseType<ProfileSetupStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProfileSetupStatusDto>> GetSetupStatus(
        string module, [FromServices] IProfileSetupService setupService, CancellationToken cancellationToken) =>
        Ok(await setupService.GetSetupStatusAsync(module, cancellationToken));

    [NonAction]
    [HttpPost("submit")]
    [ProducesResponseType<ProfileSetupSubmissionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileSetupSubmissionResponse>> Submit(
        string module, [FromBody] ProfileSetupSubmissionRequest request, [FromServices] IProfileSetupService setupService, CancellationToken cancellationToken) =>
        Ok(await setupService.SubmitProfileAsync(module, request, cancellationToken));

    [HttpPut]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<PrivateProfileDto>> Put(string module, [FromBody] JsonElement request, CancellationToken cancellationToken)
        => Ok(await profiles.UpdateAsync(module, ReadDraft(request), cancellationToken));

    private static ProfileEdit ReadDraft(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new ProfileValidationException();
        try
        {
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in request.EnumerateObject())
            {
                var key = field.Name;
                if (key is not ("displayName" or "bio" or "visibility") || !fields.TryAdd(key, field.Value))
                    throw new ProfileValidationException();
            }
            if (!fields.TryGetValue("displayName", out var name) || name.ValueKind != JsonValueKind.String
                || !fields.TryGetValue("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String
                || !fields.TryGetValue("bio", out var bio) || bio.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ProfileValidationException();

            return new(name.GetString()!, bio.GetString(), visibility.GetString()!);
        }
        catch (InvalidOperationException)
        {
            // JsonElement defers decoding property names and values, including surrogate validation.
            throw new ProfileValidationException();
        }
    }

    [HttpGet]
    public async Task<ActionResult<PrivateProfileDto>> Get(string module, CancellationToken cancellationToken) =>
        Ok(await profiles.GetAsync(module, cancellationToken));

    // Shell creation accepts an empty object. Draft fields are set using PUT.
    [HttpPost]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<PrivateProfileDto>> Post(string module, [FromBody] JsonElement request,
        CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object || request.EnumerateObject().Any())
            throw new ProfileValidationException();
        return Ok(await profiles.CreateAsync(module, cancellationToken));
    }
}
