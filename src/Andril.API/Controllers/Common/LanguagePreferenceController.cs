using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Preferences;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/preferences/language")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[LanguagePreferenceNoStore]
public sealed class LanguagePreferenceController(ILanguagePreferenceService preferences) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LanguagePreferenceDto>> Get(CancellationToken cancellationToken) =>
        Ok(await preferences.GetAsync(cancellationToken));

    [HttpPatch]
    public async Task<ActionResult<LanguagePreferenceDto>> Patch([FromBody] LanguagePreferenceRequest request,
        CancellationToken cancellationToken) =>
        Ok(await preferences.UpdateAsync(request.Language, request.Mode, cancellationToken));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LanguagePreferenceRequest
{
    [Required(ErrorMessage = "language is required.")]
    [AllowedValues("en", "ta", "si", ErrorMessage = "language must be en, ta or si.")]
    public string? Language { get; init; }

    [Required(ErrorMessage = "mode is required.")]
    [AllowedValues("sync", "settings", ErrorMessage = "mode must be sync or settings.")]
    public string? Mode { get; init; }
}

// Include automatic model-binding/validation failures, which can short-circuit action filters.
public sealed class LanguagePreferenceNoStoreAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context) => context.HttpContext.Response.Headers.CacheControl = "no-store";
    public void OnResultExecuted(ResultExecutedContext context) { }
}
