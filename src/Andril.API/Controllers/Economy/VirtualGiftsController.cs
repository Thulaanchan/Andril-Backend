using Andril.Economy.VirtualGifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Economy;

[ApiController]
[Authorize]
[Route("api/v1/economy/gifts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class VirtualGiftsController(IVirtualGiftService giftService) : ControllerBase
{
    [HttpGet("catalog")]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<VirtualGift>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<VirtualGift>> GetCatalog()
    {
        return Ok(giftService.GetCatalog());
    }

    [HttpPost("send")]
    [ProducesResponseType<GiftTransactionReceipt>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<GiftTransactionReceipt>> SendGift(
        [FromBody] SendVirtualGiftRequest request, CancellationToken cancellationToken)
    {
        var receipt = await giftService.SendGiftAsync(request, cancellationToken);
        return Ok(receipt);
    }
}
