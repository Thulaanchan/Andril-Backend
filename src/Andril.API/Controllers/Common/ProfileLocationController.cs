using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
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
    ICurrentUser user,
    IAccountRepository accounts,
    IPrivateProfileRepository profileRepository,
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
        var account = await RequireAccountAsync(cancellationToken);
        var normModule = ValidateModule(module);

        var profile = await profileRepository.ReadAsync(account.Id, normModule, _ => { }, cancellationToken)
            ?? throw new ProfileMissingException();

        var location = await locationRepository.GetByProfileIdAsync(profile.Id, cancellationToken);
        if (location is null)
            return NotFound();

        return Ok(new ProfileLocationDto(location.ProfileId, location.Module, location.City, location.Region, location.UpdatedAtUtc));
    }

    [HttpPut("profiles/{module}/location")]
    [ProducesResponseType<ProfileLocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProfileLocationDto>> SetProfileLocation(
        string module, [FromBody] UpdateProfileLocationRequest request, CancellationToken cancellationToken)
    {
        var account = await RequireAccountAsync(cancellationToken);
        var normModule = ValidateModule(module);

        if (string.IsNullOrWhiteSpace(request?.City) || !ApprovedLocationsCatalog.IsValidLocation(request.City))
            throw new ProfileValidationException();

        var profile = await profileRepository.GetOrCreateAsync(account.Id, normModule, _ => { }, clock, cancellationToken);

        var existing = await locationRepository.GetByProfileIdAsync(profile.Id, cancellationToken);
        var now = clock.GetUtcNow();

        ProfileLocationRecord saved;
        if (existing is null)
        {
            var record = ProfileLocationRecord.Create(profile.Id, account.Id, normModule, request.City, request.Region, now);
            saved = await locationRepository.UpsertAsync(record, cancellationToken);
        }
        else
        {
            existing.Update(request.City, request.Region, now);
            saved = await locationRepository.UpsertAsync(existing, cancellationToken);
        }

        return Ok(new ProfileLocationDto(saved.ProfileId, saved.Module, saved.City, saved.Region, saved.UpdatedAtUtc));
    }

    [HttpDelete("profiles/{module}/location")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClearProfileLocation(
        string module, CancellationToken cancellationToken)
    {
        var account = await RequireAccountAsync(cancellationToken);
        var normModule = ValidateModule(module);

        var profile = await profileRepository.ReadAsync(account.Id, normModule, _ => { }, cancellationToken);
        if (profile is not null)
        {
            await locationRepository.DeleteByProfileIdAsync(profile.Id, cancellationToken);
        }

        return NoContent();
    }

    private async Task<Account> RequireAccountAsync(CancellationToken ct)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return await accounts.FindBySubjectAsync(subject, ct) ?? throw new StarterModuleAccountMissingException();
    }

    private static string ValidateModule(string module)
    {
        if (!ModuleNames.IsValid(module)) throw new ProfileValidationException();
        return module.Trim().ToLowerInvariant();
    }
}
