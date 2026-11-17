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
    [HttpPut]
    [RequestSizeLimit(16384)]
    public async Task<ActionResult<PrivateProfileDto>> Put(string module, [FromBody] JsonElement request, CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new ProfileValidationException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in request.EnumerateObject())
            if (field.Name is not ("displayName" or "bio" or "visibility") || !fields.TryAdd(field.Name, field.Value)) throw new ProfileValidationException();
        if (!fields.TryGetValue("displayName", out var name) || name.ValueKind != JsonValueKind.String
            || !fields.TryGetValue("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String
            || !fields.TryGetValue("bio", out var bio) || bio.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ProfileValidationException();
        return Ok(await profiles.UpdateAsync(module, new(name.GetString()!, bio.GetString(), visibility.GetString()!), cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<PrivateProfileDto>> Get(string module, CancellationToken cancellationToken) =>
        Ok(await profiles.GetAsync(module, cancellationToken));

    // An empty object is deliberate: no approved editable field contract exists yet.
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
