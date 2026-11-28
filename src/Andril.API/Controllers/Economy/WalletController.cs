using Andril.Economy.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Economy;

[NonController]
[ApiController]
[Authorize]
[Route("api/v1/economy/wallet")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WalletController(ICoinWalletService walletService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CoinWalletDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CoinWalletDto>> GetWallet(CancellationToken cancellationToken)
    {
        var wallet = await walletService.GetMyWalletAsync(cancellationToken);
        return Ok(wallet);
    }

    [HttpGet("history")]
    [ProducesResponseType<IReadOnlyList<CoinTransactionReceipt>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<CoinTransactionReceipt>>> GetHistory(
        [FromQuery] int limit = 20, [FromQuery] int offset = 0, CancellationToken cancellationToken = default)
    {
        var history = await walletService.GetMyHistoryAsync(limit, offset, cancellationToken);
        return Ok(history);
    }

    [HttpPost("transfer")]
    [ProducesResponseType<CoinTransactionReceipt>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CoinTransactionReceipt>> Transfer(
        [FromBody] CoinTransferRequest request, CancellationToken cancellationToken)
    {
        var receipt = await walletService.TransferCoinsAsync(
            request.RecipientAccountId, request.Amount, request.IdempotencyKey, request.Note, cancellationToken);
        return Ok(receipt);
    }
}
