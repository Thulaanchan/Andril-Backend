using Andril.Common.Accounts.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController, Authorize]
[Route("api/v1/account/profiles/{module}/photos")]
public sealed class ProfilePhotosController(IProfilePhotoService photos) : ControllerBase
{
    [HttpPost("{requestId:guid}")]
    [Consumes("application/octet-stream")]
    [RequestSizeLimit(ProfilePhotoLimits.UploadBytes)]
    public async Task<ActionResult<PrivatePhotoStatus>> Post(string module, Guid requestId, CancellationToken cancellationToken) =>
        Ok(await photos.UploadAsync(module, requestId, Request.Body, cancellationToken));

    [HttpGet("{uploadId:guid}")]
    public async Task<ActionResult<PrivatePhotoStatus>> Get(string module, Guid uploadId, CancellationToken cancellationToken) =>
        Ok(await photos.GetAsync(module, uploadId, cancellationToken));

    [HttpGet("current")]
    public async Task<ActionResult<PrivatePhotoStatus>> Current(string module, CancellationToken cancellationToken) =>
        Ok(await photos.GetCurrentAsync(module, cancellationToken));

    [HttpGet("{uploadId:guid}/content")]
    public async Task<IActionResult> Content(string module, Guid uploadId, CancellationToken cancellationToken)
    {
        var bytes = await photos.GetContentAsync(module, uploadId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "image/jpeg");
    }
}
