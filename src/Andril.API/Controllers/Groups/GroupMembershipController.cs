using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Andril.API.Authorization.Groups;
using Andril.API.Controllers.Social;
using Andril.Groups.Interfaces;
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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupJoinSubmissionRequest(
    [property: JsonRequired] string Module,
    [property: JsonRequired] GroupKind GroupKind);

/// <summary>
/// Request payload for cancelling a pending group join request.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupCancelJoinRequest(
    [property: JsonRequired] string Module,
    [property: JsonRequired] GroupKind GroupKind);

/// <summary>
/// Request payload for processing a pending group join request (approval or rejection).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupProcessAdmissionRequest(
    [property: JsonRequired] string Module,
    [property: JsonRequired] GroupKind GroupKind,
    Guid ApplicantProfileId,
    [property: JsonRequired] bool Approve, Guid? RequestId = null);

/// <summary>
/// Request payload for leaving a group.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupDepartureRequest(
    [property: JsonRequired] string Module,
    [property: JsonRequired] GroupKind GroupKind);

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
[GroupErrors, SafeGroupModelState]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[Route("api/v1/groups")]
public sealed class GroupMembershipController : ControllerBase
{
    private readonly IGroupMembershipOperationService _membershipOperationService;
    private readonly GroupManagementService? _management;

    public GroupMembershipController(IGroupMembershipOperationService membershipOperationService, GroupManagementService? management = null)
    {
        _membershipOperationService = membershipOperationService ?? throw new ArgumentNullException(nameof(membershipOperationService));
        _management = management;
    }

    /// <summary>
    /// Submits a join request to the specified group under strict caller authorization and scope validation.
    /// </summary>
    [HttpPost("{groupId:guid}/join-requests")]
    [StrictJsonBody]
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
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = "The requested group kind is not compatible with the specified module scope."
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

            return !result.Success && _management is not null
                ? GroupErrorsAttribute.Problem(409, result.TransactionStatus == GroupMembershipTransactionStatus.CapacityExceeded ? "club_capacity" : "participation_conflict")
                : Ok(dto);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Join request unauthorized.",
                Detail = "Caller is not authorized to submit a join request for the specified group."
            });
        }
        catch (InvalidOperationException error) when (!GroupAvailability.IsUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid join request operation.",
                Detail = "The join request operation could not be completed in the current group state."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid join request arguments.",
                Detail = "The join request operation could not be completed with the provided arguments."
            });
        }
    }

    /// <summary>
    /// Cancels a pending join request to the specified group.
    /// </summary>
    [HttpPost("{groupId:guid}/join-requests/cancel")]
    [HttpDelete("{groupId:guid}/join-requests")]
    [StrictJsonBody]
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
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = "The requested group kind is not compatible with the specified module scope."
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

            return !result.Success && _management is not null
                ? GroupErrorsAttribute.Problem(409, result.TransactionStatus == GroupMembershipTransactionStatus.CapacityExceeded ? "club_capacity" : "participation_conflict")
                : Ok(dto);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Cancel join request unauthorized.",
                Detail = "Caller is not authorized to cancel a join request for the specified group."
            });
        }
        catch (InvalidOperationException error) when (!GroupAvailability.IsUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid cancel operation.",
                Detail = "The cancel join request operation could not be completed in the current group state."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid cancel arguments.",
                Detail = "The cancel join request operation could not be completed with the provided arguments."
            });
        }
    }

    /// <summary>
    /// Processes a pending group join request (approval or rejection) using authoritative administrator/moderator role.
    /// </summary>
    [HttpPost("{groupId:guid}/admissions")]
    [StrictJsonBody]
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

        if (request.RequestId == Guid.Empty || (request.RequestId.HasValue == (request.ApplicantProfileId != Guid.Empty)) || (request.RequestId.HasValue && _management is null))
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
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = "The requested group kind is not compatible with the specified module scope."
            });
        }

        try
        {
            var result = request.RequestId is { } requestId
                ? await _management!.DecideAsync(groupId, scope, requestId, request.Approve, cancellationToken)
                : await _membershipOperationService.ProcessJoinRequestStrictAsync(
                groupId,
                scope,
                request.ApplicantProfileId,
                request.Approve,
                cancellationToken);

            var dto = new GroupJoinOperationResponseDto(
                Success: result.Success,
                MembershipCreated: result.MembershipCreated,
                EntityRemoved: result.EntityRemoved);

            return !result.Success && _management is not null
                ? GroupErrorsAttribute.Problem(409, result.TransactionStatus == GroupMembershipTransactionStatus.CapacityExceeded ? "club_capacity" : "participation_conflict")
                : Ok(dto);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Admission processing unauthorized.",
                Detail = "Caller is not authorized to process admissions for the specified group."
            });
        }
        catch (InvalidOperationException error) when (!GroupAvailability.IsUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid admission operation.",
                Detail = "The admission processing operation could not be completed in the current group state."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid admission arguments.",
                Detail = "The admission processing operation could not be completed with the provided arguments."
            });
        }
    }

    /// <summary>
    /// Voluntarily departs (leaves) a group under strict actor ownership verification.
    /// </summary>
    [HttpPost("{groupId:guid}/leave")]
    [HttpDelete("{groupId:guid}/members/me")]
    [StrictJsonBody]
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
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Incompatible group module scope.",
                Detail = "The requested group kind is not compatible with the specified module scope."
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

            return !result.Success && _management is not null
                ? GroupErrorsAttribute.Problem(409, result.TransactionStatus == GroupMembershipTransactionStatus.CapacityExceeded ? "club_capacity" : "participation_conflict")
                : Ok(dto);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Leave group unauthorized.",
                Detail = "Caller is not authorized to leave the specified group."
            });
        }
        catch (InvalidOperationException error) when (!GroupAvailability.IsUnavailable(error))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid leave operation.",
                Detail = "The leave group operation could not be completed in the current group state."
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid leave arguments.",
                Detail = "The leave group operation could not be completed with the provided arguments."
            });
        }
    }
}
