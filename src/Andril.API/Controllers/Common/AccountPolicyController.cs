using System.Text.Json.Serialization;
using Andril.Common.Accounts.Policies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/policies")]
public sealed class AccountPolicyController(IAccountPolicyService policies) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<PolicyStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await policies.GetAsync(cancellationToken));

    [HttpPost("acknowledgments")]
    public async Task<ActionResult<PolicyStatus>> Post([FromBody] PolicyAcknowledgmentRequest request, CancellationToken cancellationToken) =>
        Ok(await policies.AcknowledgeAsync(request.Actions?.Select(x => x is null
            ? new PolicyActionSubmission(null, null) : new PolicyActionSubmission(x.Action, x.Version)).ToArray(), cancellationToken));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PolicyAcknowledgmentRequest
{
    public PolicyActionRequest?[]? Actions { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PolicyActionRequest
{
    public string? Action { get; init; }
    public string? Version { get; init; }
}
