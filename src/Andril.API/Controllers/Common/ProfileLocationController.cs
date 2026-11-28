using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.API.Controllers.Common;

[NonController]
[ApiController]
[Authorize]
[Route("api/v1/account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfileLocationController(
    IAccountService accounts,
    IPrivateProfileService profiles,
    IProfileLocationRepository locationRepository,
    TimeProvider clock)
    : ControllerBase
{
    [HttpGet("locations")]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<ApprovedLocationItem>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ApprovedLocationItem>> GetApprovedLocations() =>
        Ok(ApprovedLocationsCatalog.GetApprovedLocations());

    [HttpGet("profiles/{module}/location")]
    [ProducesResponseType<ProfileLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileLocationDto>> GetProfileLocation(
        string module, CancellationToken cancellationToken)
    {
        var account = await accounts.GetMeAsync(cancellationToken) ?? throw new StarterModuleAccountMissingException();
        var normModule = ValidateModule(module);
        return await profiles.WithAuthorizedProfileAsync<ActionResult<ProfileLocationDto>>(normModule, async profile =>
        {
            var location = await locationRepository.GetByProfileIdAsync(profile.ProfileId, cancellationToken);
            if (location is null) return NotFound();
            CheckScope(location, profile.ProfileId, account.AccountId, normModule);
            return Ok(ToDto(location));
        }, cancellationToken);
    }

    [HttpPut("profiles/{module}/location")]
    [ProducesResponseType<ProfileLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProfileLocationDto>> SetProfileLocation(
        string module, [FromBody] UpdateProfileLocationRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetMeAsync(cancellationToken) ?? throw new StarterModuleAccountMissingException();
        var normModule = ValidateModule(module);
        if (string.IsNullOrWhiteSpace(request?.City) || !ApprovedLocationsCatalog.IsValidLocation(request.City))
            throw new ProfileValidationException();
        return await profiles.WithAuthorizedProfileAsync<ActionResult<ProfileLocationDto>>(normModule, async profile =>
        {
            var existing = await locationRepository.GetByProfileIdAsync(profile.ProfileId, cancellationToken);
            if (existing is not null) CheckScope(existing, profile.ProfileId, account.AccountId, normModule);
            var now = clock.GetUtcNow();
            var record = existing ?? ProfileLocationRecord.Create(profile.ProfileId, account.AccountId, normModule, request.City, request.Region, now);
            if (existing is not null) record.Update(request.City, request.Region, now);
            var saved = await locationRepository.UpsertAsync(record, cancellationToken);
            CheckScope(saved, profile.ProfileId, account.AccountId, normModule);
            return Ok(ToDto(saved));
        }, cancellationToken);
    }

    [HttpDelete("profiles/{module}/location")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClearProfileLocation(
        string module, CancellationToken cancellationToken)
    {
        var account = await accounts.GetMeAsync(cancellationToken) ?? throw new StarterModuleAccountMissingException();
        var normModule = ValidateModule(module);
        return await profiles.WithAuthorizedProfileAsync<IActionResult>(normModule, async profile =>
        {
            var existing = await locationRepository.GetByProfileIdAsync(profile.ProfileId, cancellationToken);
            if (existing is not null)
            {
                CheckScope(existing, profile.ProfileId, account.AccountId, normModule);
                await locationRepository.DeleteByProfileIdAsync(profile.ProfileId, cancellationToken);
            }
            return NoContent();
        }, cancellationToken);
    }

    private static void CheckScope(ProfileLocationRecord record, Guid profileId, Guid accountId, string module)
    {
        if (record.ProfileId != profileId || record.AccountId != accountId || record.Module != module)
            throw new ProfileAccessDeniedException();
    }

    private static ProfileLocationDto ToDto(ProfileLocationRecord record) =>
        new(record.ProfileId, record.Module, record.City, record.Region, record.UpdatedAtUtc);

    private static string ValidateModule(string module)
    {
        if (!ModuleNames.IsValid(module)) throw new ProfileValidationException();
        return module.Trim().ToLowerInvariant();
    }
}
