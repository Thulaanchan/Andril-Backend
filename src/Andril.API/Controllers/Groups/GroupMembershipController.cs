using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Groups;

/// <summary>
/// Request payload for submitting a group join request.
/// </summary>
public sealed record GroupJoinSubmissionRequest(
    string Module,
    GroupKind GroupKind);

/// <summary>
/// Request payload for cancelling a pending group join request.
/// </summary>
public sealed record GroupCancelJoinRequest(
    string Module,
    GroupKind GroupKind);

/// <summary>
/// Request payload for processing a pending group join request (approval or rejection).
/// </summary>
public sealed record GroupProcessAdmissionRequest(
    string Module,
    GroupKind GroupKind,
    Guid ApplicantProfileId,
    bool Approve);

/// <summary>
/// Request payload for leaving a group.
/// </summary>
public sealed record GroupDepartureRequest(
    string Module,
    GroupKind GroupKind);

/// <summary>
/// Privacy-safe response projecting the outcome of an authorized group membership operation.
/// </summary>
public sealed record GroupJoinOperationResponseDto(
    bool Success,
    bool MembershipCreated,
    bool EntityRemoved);

/// <summary>
/// Controller for authorized Group membership operations: join request submission,
/// cancellation, role-governed admission processing, and voluntary exit (CF-07).
/// Enforces trusted caller scope, module boundaries (Classroom, Club, Zone),
/// capacity restrictions, and atomic transactions via <see cref="IGroupMembershipOperationService"/>.
/// Excluded from automatic MVC discovery via [NonController] until persistence activation is approved.
/// </summary>
[ApiController]
[Authorize]
[NonController]
[Route("api/v1/groups")]
public sealed class GroupMembershipController : ControllerBase
{
    private readonly IGroupMembershipOperationService _membershipOperationService;

    public GroupMembershipController(IGroupMembershipOperationService membershipOperationService)
    {
        _membershipOperationService = membershipOperationService ?? throw new ArgumentNullException(nameof(membershipOperationService));
    }

    /// <summary>
    /// Submits a join request to the specified group under strict caller authorization and scope validation.
    /// </summary>
    [HttpPost("{groupId:guid}/join-requests")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<GroupJoinOperationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GroupJoinOperationResponseDto>> SubmitJoinRequest(
        [FromRoute] Guid groupId,
        [FromBody] GroupJoinSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid group identity.",
                Detail = "Group identity must be a non-empty GUID."
            });
        }

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request payload.",
                Detail = "Request body cannot be null."
            });
        }

        GroupModuleScope scope;
        try
        {
            scope = new GroupModuleScope(request.Module, request.GroupKind);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = ex.Message
            });
        }

        try
        {
            var result = await _membershipOperationService.SubmitJoinRequestStrictAsync(
                groupId,
                scope,
                cancellationToken);

            var dto = new GroupJoinOperationResponseDto(
                Success: result.Success,
                MembershipCreated: result.MembershipCreated,
                EntityRemoved: result.EntityRemoved);

            return Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Join request unauthorized.",
                Detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid join request operation.",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid join request arguments.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Cancels a pending join request to the specified group.
    /// </summary>
    [HttpPost("{groupId:guid}/join-requests/cancel")]
    [HttpDelete("{groupId:guid}/join-requests")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<GroupJoinOperationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GroupJoinOperationResponseDto>> CancelJoinRequest(
        [FromRoute] Guid groupId,
        [FromBody] GroupCancelJoinRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid group identity.",
                Detail = "Group identity must be a non-empty GUID."
            });
        }

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request payload.",
                Detail = "Request body cannot be null."
            });
        }

        GroupModuleScope scope;
        try
        {
            scope = new GroupModuleScope(request.Module, request.GroupKind);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = ex.Message
            });
        }

        try
        {
            var result = await _membershipOperationService.CancelJoinRequestStrictAsync(
                groupId,
                scope,
                cancellationToken);

            var dto = new GroupJoinOperationResponseDto(
                Success: result.Success,
                MembershipCreated: result.MembershipCreated,
                EntityRemoved: result.EntityRemoved);

            return Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Cancel join request unauthorized.",
                Detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid cancel operation.",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid cancel arguments.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Processes a pending group join request (approval or rejection) using authoritative administrator/moderator role.
    /// </summary>
    [HttpPost("{groupId:guid}/admissions")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<GroupJoinOperationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GroupJoinOperationResponseDto>> ProcessAdmission(
        [FromRoute] Guid groupId,
        [FromBody] GroupProcessAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid group identity.",
                Detail = "Group identity must be a non-empty GUID."
            });
        }

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request payload.",
                Detail = "Request body cannot be null."
            });
        }

        if (request.ApplicantProfileId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid applicant profile identity.",
                Detail = "Applicant profile identity must be a non-empty GUID."
            });
        }

        GroupModuleScope scope;
        try
        {
            scope = new GroupModuleScope(request.Module, request.GroupKind);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = ex.Message
            });
        }

        try
        {
            var result = await _membershipOperationService.ProcessJoinRequestStrictAsync(
                groupId,
                scope,
                request.ApplicantProfileId,
                request.Approve,
                cancellationToken);

            var dto = new GroupJoinOperationResponseDto(
                Success: result.Success,
                MembershipCreated: result.MembershipCreated,
                EntityRemoved: result.EntityRemoved);

            return Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Admission processing unauthorized.",
                Detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid admission operation.",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid admission arguments.",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Voluntarily departs (leaves) a group under strict actor ownership verification.
    /// </summary>
    [HttpPost("{groupId:guid}/leave")]
    [HttpDelete("{groupId:guid}/members/me")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<GroupJoinOperationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GroupJoinOperationResponseDto>> LeaveGroup(
        [FromRoute] Guid groupId,
        [FromBody] GroupDepartureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid group identity.",
                Detail = "Group identity must be a non-empty GUID."
            });
        }

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request payload.",
                Detail = "Request body cannot be null."
            });
        }

        GroupModuleScope scope;
        try
        {
            scope = new GroupModuleScope(request.Module, request.GroupKind);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = ex.Message
            });
        }

        try
        {
            var result = await _membershipOperationService.LeaveGroupStrictAsync(
                groupId,
                scope,
                cancellationToken);

            var dto = new GroupJoinOperationResponseDto(
                Success: result.Success,
                MembershipCreated: result.MembershipCreated,
                EntityRemoved: result.EntityRemoved);

            return Ok(dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Leave group unauthorized.",
                Detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid leave operation.",
                Detail = ex.Message
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid leave arguments.",
                Detail = ex.Message
            });
        }
    }
}
