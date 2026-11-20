
using Andril.API.Authentication;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
[ResponseCache(
    NoStore = true,
    Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(
    IAccountService accounts,
    IOnboardingIdentity identity,
    NotificationApplicationService notifications)
    : ControllerBase
{
    // Only supported module names are accepted.
    // Do not accept arbitrary enum numbers.
    private static NotificationModule? ParseModule(
        string? module)
    {
        return module?.Trim().ToLowerInvariant() switch
        {
            "learning" => NotificationModule.Learning,
            "hobbies" => NotificationModule.Hobbies,
            "social" => NotificationModule.Social,
            _ => null
        };
    }

    // GET /api/v1/notifications/{module}
    [HttpGet("{module}")]
    public async Task<IActionResult> List(
        string module,
        [FromQuery] int maxItems = 20,
        CancellationToken cancellationToken = default)
    {
        if (VerifiedUserSubject.GetId(User) is not Guid subject ||
     !identity.IsConfirmedFor(subject))
        {
            return StatusCode(403);
        }

        var selectedModule = ParseModule(module);

        if (selectedModule is null)
            return NotFound();

        if (maxItems < 1 || maxItems > 100)
            return BadRequest();

        var account = await accounts.GetMeAsync(
            cancellationToken);

        if (account is null)
            return NotFound();

        var result = await notifications.ListLatestAsync(
            account.AccountId,
            selectedModule.Value,
            maxItems,
            cancellationToken);

        return Ok(result);
    }

    // GET /api/v1/notifications/{module}/{id}
    [HttpGet("{module}/{notificationId:guid}")]
    public async Task<IActionResult> Get(
        string module,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        if (VerifiedUserSubject.GetId(User) is not Guid subject ||
    !identity.IsConfirmedFor(subject))
        {
            return StatusCode(403);
        }

        var selectedModule = ParseModule(module);

        if (selectedModule is null ||
            notificationId == Guid.Empty)
            return NotFound();

        var account = await accounts.GetMeAsync(
            cancellationToken);

        if (account is null)
            return NotFound();

        var result = await notifications.FindAsync(
            account.AccountId,
            selectedModule.Value,
            notificationId,
            cancellationToken);

        return result is null
            ? NotFound()
            : Ok(result);
    }

    // PATCH /api/v1/notifications/{module}/{id}/read
    [HttpPatch("{module}/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(
        string module,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        if (VerifiedUserSubject.GetId(User) is not Guid subject ||
    !identity.IsConfirmedFor(subject))
        {
            return StatusCode(403);
        }

        var selectedModule = ParseModule(module);

        if (selectedModule is null ||
            notificationId == Guid.Empty)
            return NotFound();

        var account = await accounts.GetMeAsync(
            cancellationToken);

        if (account is null)
            return NotFound();

        var updated = await notifications.TryMarkReadAsync(
            account.AccountId,
            selectedModule.Value,
            notificationId,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return updated
            ? NoContent()
            : NotFound();
    }
}
