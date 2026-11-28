using System.Text.Json.Serialization;
using Andril.API.Authorization.Groups;
using Andril.API.Controllers.Social;
using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Groups;

[ApiController, Authorize, NonController, GroupErrors, SafeGroupModelState]
[Route("api/v1/groups")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ProducesResponseType<ProblemDetails>(503)]
public sealed class GroupManagementController(GroupManagementService management, PostgresGroupAuthorityWriter authority) : ControllerBase
{
    [HttpPost, StrictJsonBody]
    [ProducesResponseType<GroupDetailsDto>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<GroupDetailsDto>> Create([FromBody] CreateGroupRequest request, CancellationToken token)
    {
        var scope = new GroupModuleScope(request.Module, request.GroupKind);
        var created = await management.CreateAsync(scope,
            new(request.Name, request.Description, request.IsDiscoverable, request.Visibility ?? "private"), token);
        return CreatedAtAction(nameof(Details), new { groupId = created.Id, module = created.Module, groupKind = (int)created.GroupKind }, created);
    }

    [HttpGet("discover")]
    [ProducesResponseType<IReadOnlyList<GroupDiscoveryCardDto>>(200)]
    public async Task<ActionResult<IReadOnlyList<GroupDiscoveryCardDto>>> Discover([FromQuery] string module,
        [FromQuery] GroupKind groupKind, [FromQuery] string? search, [FromQuery] int? maximum, CancellationToken token)
    {
        var scope = new GroupModuleScope(module, groupKind);
        if (search?.Length > 200) throw new ArgumentException("Search is too long.");
        var cards = await management.DiscoverAsync(scope, search ?? string.Empty, ValidateMaximum(maximum), token);
        return cards is null ? GroupErrorsAttribute.Problem(403) : Ok(cards);
    }

    [HttpGet("{groupId:guid}")]
    [ProducesResponseType<GroupDetailsDto>(200)]
    public async Task<ActionResult<GroupDetailsDto>> Details(Guid groupId, [FromQuery] string module, [FromQuery] GroupKind groupKind,
        CancellationToken token)
    {
        ValidateId(groupId);
        var result = await management.ReadAsync(groupId, new(module, groupKind), token);
        return result is null ? GroupErrorsAttribute.Problem(403) : Ok(result);
    }

    [HttpPut("{groupId:guid}/configuration"), StrictJsonBody]
    [ProducesResponseType<GroupDetailsDto>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<GroupDetailsDto>> Configure(Guid groupId, [FromBody] UpdateGroupRequest request, CancellationToken token)
    {
        ValidateId(groupId);
        var result = await management.UpdateAsync(groupId, new(request.Module, request.GroupKind),
            new(request.Name, request.Description, request.IsDiscoverable, request.Visibility), request.ExpectedRevision, token);
        return result is null ? GroupErrorsAttribute.Problem(409, "revision_conflict") : Ok(result);
    }

    [HttpGet("{groupId:guid}/participation/me")]
    [ProducesResponseType<GroupPersonalParticipationDto>(200)]
    public async Task<ActionResult<GroupPersonalParticipationDto>> OwnStatus(Guid groupId, [FromQuery] string module,
        [FromQuery] GroupKind groupKind, CancellationToken token)
    {
        ValidateId(groupId);
        var result = await management.GetOwnStatusAsync(groupId, new(module, groupKind), token);
        return result is null ? GroupErrorsAttribute.Problem(403) : Ok(result);
    }

    [HttpGet("{groupId:guid}/admissions")]
    [ProducesResponseType<IReadOnlyList<GroupPendingRequestDto>>(200)]
    public async Task<ActionResult<IReadOnlyList<GroupPendingRequestDto>>> Pending(Guid groupId, [FromQuery] string module,
        [FromQuery] GroupKind groupKind, [FromQuery] int? maximum, CancellationToken token)
    {
        ValidateId(groupId);
        return Ok(await management.ReadPendingAsync(groupId, new(module, groupKind), ValidateMaximum(maximum), token));
    }

    [HttpPut("safety/{module}/blocks/{profileId:guid}"), StrictJsonBody]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Block(string module, Guid profileId, [FromBody] GroupAuthorityRequest request, CancellationToken token)
    {
        await authority.BlockAsync(module, profileId, request.Enabled, token);
        return NoContent();
    }

    [HttpPut("safety/{module}/restrictions/{profileId:guid}"), StrictJsonBody]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Restrict(string module, Guid profileId, [FromBody] GroupAuthorityRequest request, CancellationToken token)
    {
        await authority.RestrictAsync(module, profileId, request.Enabled, token);
        return NoContent();
    }

    [HttpPut("learning/educators/{profileId:guid}"), StrictJsonBody]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SetEducator(Guid profileId, [FromBody] GroupAuthorityRequest request, CancellationToken token)
    {
        await authority.SetClassroomHostAsync(profileId, request.Enabled, token);
        return NoContent();
    }

    private static int ValidateMaximum(int? maximum) => maximum switch
    {
        null => 25,
        >= 1 and <= GroupManagementService.MaximumResults => maximum.Value,
        _ => throw new ArgumentException("The result limit is outside its allowed range.")
    };

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A valid Group identity is required.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGroupRequest([property: JsonRequired] string Module, [property: JsonRequired] GroupKind GroupKind,
    [property: JsonRequired] string Name, [property: JsonRequired] bool IsDiscoverable, string? Description = null, string? Visibility = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateGroupRequest([property: JsonRequired] string Module, [property: JsonRequired] GroupKind GroupKind,
    [property: JsonRequired] string Name, [property: JsonRequired] bool IsDiscoverable, [property: JsonRequired] string Visibility,
    [property: JsonRequired] long ExpectedRevision, string? Description = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupAuthorityRequest([property: JsonRequired] bool Enabled);
