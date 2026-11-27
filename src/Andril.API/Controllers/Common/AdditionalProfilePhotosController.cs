using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.DTOs;
using Andril.Common.Interfaces;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/profiles/{module}/photos/additional")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdditionalProfilePhotosController(
    ICurrentUser user,
    IAccountRepository accounts,
    IPrivateProfileRepository profileRepository,
    IAdditionalProfilePhotoRepository additionalPhotos,
    TimeProvider clock,
    IPrivateProfilePhotoObjectStorage? objects = null)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AdditionalPhotosListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdditionalPhotosListDto>> GetSlots(
        string module, CancellationToken cancellationToken)
    {
        var (account, profile) = await AuthorizeAndGetProfileAsync(module, cancellationToken);
        var photos = await additionalPhotos.GetByProfileIdAsync(profile.Id, cancellationToken);

        var slots = new List<AdditionalPhotoSlotDto>(3);
        for (int i = 1; i <= 3; i++)
        {
            var photo = photos.FirstOrDefault(p => p.SlotIndex == i);
            slots.Add(new(i, photo?.Id, photo is not null, photo?.ObjectKey, photo?.UpdatedAtUtc));
        }

        return Ok(new AdditionalPhotosListDto(module, 3, slots));
    }

    [HttpGet("{slotIndex:int}")]
    [ProducesResponseType<AdditionalPhotoSlotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdditionalPhotoSlotDto>> GetSlot(
        string module, int slotIndex, CancellationToken cancellationToken)
    {
        if (slotIndex is < 1 or > 3)
            throw new InvalidPhotoSlotException($"Slot index must be between 1 and 3. Received: {slotIndex}");

        var (account, profile) = await AuthorizeAndGetProfileAsync(module, cancellationToken);
        var photo = await additionalPhotos.GetByProfileAndSlotAsync(profile.Id, slotIndex, cancellationToken);
        if (photo is null) return NotFound();

        return Ok(new AdditionalPhotoSlotDto(photo.SlotIndex, photo.Id, true, photo.ObjectKey, photo.UpdatedAtUtc));
    }

    [HttpGet("{slotIndex:int}/content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSlotContent(
        string module, int slotIndex, CancellationToken cancellationToken)
    {
        if (slotIndex is < 1 or > 3)
            throw new InvalidPhotoSlotException($"Slot index must be between 1 and 3. Received: {slotIndex}");

        var (account, profile) = await AuthorizeAndGetProfileAsync(module, cancellationToken);
        var photo = await additionalPhotos.GetByProfileAndSlotAsync(profile.Id, slotIndex, cancellationToken);
        if (photo is null) return NotFound();

        if (objects is null || !objects.IsConfigured)
        {
            Response.Headers.CacheControl = "private, no-store";
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Status = 503, Title = "Photo storage unavailable" });
        }

        var address = new PhotoObjectAddress(new PhotoScope(profile.Id, profile.Module), photo.Id);
        var expected = new StoredProfilePhoto(address, PhotoState.Stored, true, "", photo.MediaType, photo.FileSizeBytes, 0, 0);
        var read = await objects.ReadAsync(expected, cancellationToken);
        if (read.Outcome != PhotoObjectOutcome.Success || read.Bytes is null)
            return NotFound();

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(read.Bytes, photo.MediaType);
    }

    [HttpPost("{slotIndex:int}")]
    [Consumes("application/octet-stream")]
    [RequestSizeLimit(ProfilePhotoLimits.UploadBytes)]
    [ProducesResponseType<AdditionalPhotoSlotDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdditionalPhotoSlotDto>> UploadToSlot(
        string module, int slotIndex, CancellationToken cancellationToken)
    {
        if (slotIndex is < 1 or > 3)
            throw new InvalidPhotoSlotException($"Slot index must be between 1 and 3. Received: {slotIndex}");

        var (account, profile) = await AuthorizeAndGetProfileAsync(module, cancellationToken);

        // Read stream bytes
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();

        if (bytes.Length == 0)
            throw new ProfileValidationException();
        if (bytes.Length > ProfilePhotoLimits.UploadBytes)
            throw new PhotoUploadTooLargeException();
        if (!IsSupportedImage(bytes))
            throw new PhotoUploadInvalidException();

        var now = clock.GetUtcNow();
        var photoId = Guid.NewGuid();
        var address = new PhotoObjectAddress(new PhotoScope(profile.Id, profile.Module), photoId);

        // Actual private object storage write if storage is configured
        if (objects is not null && objects.IsConfigured)
        {
            var hash = PhotoHashes.Compute(bytes);
            var expected = new StoredProfilePhoto(address, PhotoState.Stored, true, hash, "image/jpeg", bytes.Length, 0, 0);
            var outcome = await objects.UploadAsync(expected, bytes, cancellationToken);
            if (outcome != PhotoObjectOutcome.Success)
                throw new PhotoDependencyException();
        }

        var objectKey = address.Key;
        var existing = await additionalPhotos.GetByProfileAndSlotAsync(profile.Id, slotIndex, cancellationToken);
        AdditionalProfilePhotoRecord saved;

        try
        {
            if (existing is null)
            {
                var record = AdditionalProfilePhotoRecord.Create(
                    profile.Id, account.Id, profile.Module, slotIndex, objectKey, $"slot_{slotIndex}.jpg", "image/jpeg", bytes.Length, now);
                saved = await additionalPhotos.UpsertSlotPhotoAsync(record, cancellationToken);
            }
            else
            {
                existing.Replace(objectKey, $"slot_{slotIndex}.jpg", "image/jpeg", bytes.Length, now);
                saved = await additionalPhotos.UpsertSlotPhotoAsync(existing, cancellationToken);
            }
        }
        catch
        {
            // Compensation cleanup if database persistence fails
            if (objects is not null && objects.IsConfigured)
            {
                try { await objects.DeleteAsync(address, CancellationToken.None); } catch { }
            }
            throw;
        }

        return Ok(new AdditionalPhotoSlotDto(saved.SlotIndex, saved.Id, true, saved.ObjectKey, saved.UpdatedAtUtc));
    }

    [HttpDelete("{slotIndex:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSlot(
        string module, int slotIndex, CancellationToken cancellationToken)
    {
        if (slotIndex is < 1 or > 3)
            throw new InvalidPhotoSlotException($"Slot index must be between 1 and 3. Received: {slotIndex}");

        var (account, profile) = await AuthorizeAndGetProfileAsync(module, cancellationToken);
        var existing = await additionalPhotos.GetByProfileAndSlotAsync(profile.Id, slotIndex, cancellationToken);
        if (existing is not null)
        {
            await additionalPhotos.DeleteSlotPhotoAsync(profile.Id, slotIndex, cancellationToken);
            if (objects is not null && objects.IsConfigured)
            {
                var address = new PhotoObjectAddress(new PhotoScope(profile.Id, profile.Module), existing.Id);
                try { await objects.DeleteAsync(address, cancellationToken); } catch { }
            }
        }

        return NoContent();
    }

    private static bool IsSupportedImage(byte[] bytes)
    {
        // JPEG magic: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        // PNG magic: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return true;
        return false;
    }

    private async Task<(Account Account, PrivateModuleProfile Profile)> AuthorizeAndGetProfileAsync(
        string module, CancellationToken ct)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        if (!ModuleNames.IsValid(module))
            throw new ProfileValidationException();

        var account = await accounts.FindBySubjectAsync(subject, ct) ?? throw new StarterModuleAccountMissingException();
        var profile = await profileRepository.GetOrCreateAsync(account.Id, module.Trim().ToLowerInvariant(), _ => { }, clock, ct);

        return (account, profile);
    }
}
