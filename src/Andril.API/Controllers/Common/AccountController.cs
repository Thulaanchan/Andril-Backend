using Andril.Common.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(IAccountService accounts) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccountDto>> GetMe(CancellationToken cancellationToken)
    {
        var account = await accounts.GetMeAsync(cancellationToken);
        return account is null ? Problem(statusCode: 404, title: "Account not initialized") : Ok(account);
    }

    [HttpPost("initialize")]
    [ProducesResponseType<AccountDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountDto>> Initialize(CancellationToken cancellationToken) =>
        Ok(await accounts.InitializeAsync(cancellationToken));
}
