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
    public async Task<ActionResult<PrivatePhotoStatus>> Post(string module, Guid requestId, CancellationToken cancellationToken) =>
        Ok(await photos.UploadAsync(module, requestId, Request.Body, cancellationToken));
    [HttpGet("{uploadId:guid}")]
    public async Task<ActionResult<PrivatePhotoStatus>> Get(string module, Guid uploadId, CancellationToken cancellationToken) =>
        Ok(await photos.GetAsync(module, uploadId, cancellationToken));
}
