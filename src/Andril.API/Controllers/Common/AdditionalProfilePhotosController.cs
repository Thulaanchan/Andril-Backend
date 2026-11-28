using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.API.Controllers.Common;

// Slot metadata alone is not finalized private-media evidence. The accepted main-photo
// protocol cannot be reused as a slot writer: it would replace the current main photo.
// Keep this draft transport closed until slot staging, delivery and cleanup are approved.
[NonController]
[ApiController]
[Authorize]
[Route("api/v1/account/profiles/{module}/photos/additional")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdditionalProfilePhotosController(ICurrentUser user) : ControllerBase
{
    [HttpGet]
    public Task<ActionResult<AdditionalPhotosListDto>> GetSlots(string module, CancellationToken cancellationToken) =>
        Task.FromException<ActionResult<AdditionalPhotosListDto>>(Blocked(module, null, cancellationToken));

    [HttpGet("{slotIndex:int}")]
    public Task<ActionResult<AdditionalPhotoSlotDto>> GetSlot(string module, int slotIndex, CancellationToken cancellationToken) =>
        Task.FromException<ActionResult<AdditionalPhotoSlotDto>>(Blocked(module, slotIndex, cancellationToken));

    [HttpGet("{slotIndex:int}/content")]
    public Task<IActionResult> GetSlotContent(string module, int slotIndex, CancellationToken cancellationToken) =>
        Task.FromException<IActionResult>(Blocked(module, slotIndex, cancellationToken));

    [HttpPost("{slotIndex:int}")]
    public Task<ActionResult<AdditionalPhotoSlotDto>> UploadToSlot(string module, int slotIndex, CancellationToken cancellationToken) =>
        Task.FromException<ActionResult<AdditionalPhotoSlotDto>>(Blocked(module, slotIndex, cancellationToken));

    [HttpDelete("{slotIndex:int}")]
    public Task<IActionResult> DeleteSlot(string module, int slotIndex, CancellationToken cancellationToken) =>
        Task.FromException<IActionResult>(Blocked(module, slotIndex, cancellationToken));

    private Exception Blocked(string module, int? slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            return new AccountAuthenticationRequiredException();
        if (!ModuleNames.IsValid(module)) return new ProfileValidationException();
        if (slot is < 1 or > 3) return new InvalidPhotoSlotException("Slot index must be between 1 and 3.");
        return new PhotoDependencyException();
    }
}
