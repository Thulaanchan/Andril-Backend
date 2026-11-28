using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Controllers.Groups;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Groups;

public sealed class GroupMembershipControllerTests
{
    #region 1. Metadata and Architectural Security Verification

    [Fact]
    public void Controller_HasNonControllerAttribute()
    {
        var nonControllerAttr = typeof(GroupMembershipController)
            .GetCustomAttribute<NonControllerAttribute>(inherit: false);

        Assert.NotNull(nonControllerAttr);
    }

    [Fact]
    public void Controller_HasApiControllerAttribute()
    {
        var apiControllerAttr = typeof(GroupMembershipController)
            .GetCustomAttribute<ApiControllerAttribute>(inherit: false);

        Assert.NotNull(apiControllerAttr);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var authorizeAttr = typeof(GroupMembershipController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        Assert.NotNull(authorizeAttr);
    }

    [Fact]
    public void Controller_HasRouteAttribute_WithCorrectTemplate()
    {
        var routeAttr = typeof(GroupMembershipController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);

        Assert.NotNull(routeAttr);
        Assert.Equal("api/v1/groups", routeAttr.Template);
    }

    [Theory]
    [InlineData(nameof(GroupMembershipController.SubmitJoinRequest))]
    [InlineData(nameof(GroupMembershipController.CancelJoinRequest))]
    [InlineData(nameof(GroupMembershipController.ProcessAdmission))]
    [InlineData(nameof(GroupMembershipController.LeaveGroup))]
    public void Endpoints_HaveResponseCacheNoStoreAttribute(string methodName)
    {
        var method = typeof(GroupMembershipController).GetMethod(methodName);
        Assert.NotNull(method);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>(inherit: false);
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public void DefaultHost_DoesNotActivateGroupMembershipController()
    {
        var isNonController = typeof(GroupMembershipController).IsDefined(typeof(NonControllerAttribute), inherit: false);
        Assert.True(isNonController, "GroupMembershipController must be marked [NonController] to prevent runtime activation.");
    }

    #endregion

    #region 2. Parameter Validation and Incompatible Scope Rejection

    [Fact]
    public void Constructor_NullOperationService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupMembershipController(null!));
    }

    [Fact]
    public async Task SubmitJoinRequest_EmptyGroupId_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        var result = await controller.SubmitJoinRequest(Guid.Empty, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task SubmitJoinRequest_NullPayload_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);

        var result = await controller.SubmitJoinRequest(Guid.NewGuid(), null!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task SubmitJoinRequest_IncompatibleScope_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        // "social" module is incompatible with GroupKind.Classroom (requires "learning")
        var request = new GroupJoinSubmissionRequest("social", GroupKind.Classroom);

        var result = await controller.SubmitJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Incompatible group module scope.", details.Title);
        Assert.Equal("The requested group kind is not compatible with the specified module scope.", details.Detail);
    }

    [Fact]
    public async Task CancelJoinRequest_EmptyGroupId_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupCancelJoinRequest("learning", GroupKind.Classroom);

        var result = await controller.CancelJoinRequest(Guid.Empty, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task CancelJoinRequest_IncompatibleScope_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupCancelJoinRequest("hobbies", GroupKind.Classroom);

        var result = await controller.CancelJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task ProcessAdmission_EmptyGroupId_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, Guid.NewGuid(), true);

        var result = await controller.ProcessAdmission(Guid.Empty, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task ProcessAdmission_EmptyApplicantProfileId_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, Guid.Empty, true);

        var result = await controller.ProcessAdmission(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task ProcessAdmission_IncompatibleScope_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("social", GroupKind.Club, Guid.NewGuid(), true);

        var result = await controller.ProcessAdmission(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task LeaveGroup_EmptyGroupId_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupDepartureRequest("learning", GroupKind.Classroom);

        var result = await controller.LeaveGroup(Guid.Empty, request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task LeaveGroup_IncompatibleScope_ReturnsBadRequest()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupDepartureRequest("learning", GroupKind.Zone);

        var result = await controller.LeaveGroup(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    #endregion

    #region 3. Join Request Submission Operations and Module Scope Isolation

    [Fact]
    public async Task SubmitJoinRequest_ClassroomScope_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            SubmitResult = GroupJoinOperationResult.RequestSubmitted()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.SubmitJoinRequest(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);
        Assert.NotNull(service.LastScope);
        Assert.Equal("learning", service.LastScope.Module);
        Assert.Equal(GroupKind.Classroom, service.LastScope.GroupKind);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MembershipCreated);
        Assert.False(dto.EntityRemoved);
    }

    [Fact]
    public async Task SubmitJoinRequest_ClubScope_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            SubmitResult = GroupJoinOperationResult.RequestSubmitted()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("hobbies", GroupKind.Club);

        var actionResult = await controller.SubmitJoinRequest(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);
        Assert.NotNull(service.LastScope);
        Assert.Equal("hobbies", service.LastScope.Module);
        Assert.Equal(GroupKind.Club, service.LastScope.GroupKind);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
    }

    [Fact]
    public async Task SubmitJoinRequest_ZoneScope_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            SubmitResult = GroupJoinOperationResult.RequestSubmitted()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("social", GroupKind.Zone);

        var actionResult = await controller.SubmitJoinRequest(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);
        Assert.NotNull(service.LastScope);
        Assert.Equal("social", service.LastScope.Module);
        Assert.Equal(GroupKind.Zone, service.LastScope.GroupKind);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
    }

    #endregion

    #region 4. Join Request Cancellation Operations

    [Fact]
    public async Task CancelJoinRequest_ValidRequest_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            CancelResult = GroupJoinOperationResult.RequestCancelled()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupCancelJoinRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.CancelJoinRequest(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MembershipCreated);
        Assert.True(dto.EntityRemoved);
    }

    #endregion

    #region 5. Admission Processing Operations

    [Fact]
    public async Task ProcessAdmission_Approval_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            ProcessResult = GroupJoinOperationResult.Approved()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, applicantId, true);

        var actionResult = await controller.ProcessAdmission(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);
        Assert.Equal(applicantId, service.LastApplicantProfileId);
        Assert.True(service.LastApprove);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.True(dto.MembershipCreated);
        Assert.True(dto.EntityRemoved);
    }

    [Fact]
    public async Task ProcessAdmission_Rejection_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            ProcessResult = GroupJoinOperationResult.Rejected()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, applicantId, false);

        var actionResult = await controller.ProcessAdmission(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);
        Assert.Equal(applicantId, service.LastApplicantProfileId);
        Assert.False(service.LastApprove);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MembershipCreated);
        Assert.True(dto.EntityRemoved);
    }

    #endregion

    #region 6. Group Departure (Leave) Operations

    [Fact]
    public async Task LeaveGroup_ValidRequest_CallsServiceAndReturnsOk()
    {
        var groupId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            LeaveResult = GroupJoinOperationResult.Left()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupDepartureRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.LeaveGroup(groupId, request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(groupId, service.LastGroupId);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MembershipCreated);
        Assert.True(dto.EntityRemoved);
    }

    #endregion

    #region 7. Authorization, Eligibility, and Transaction Failure Handling

    [Fact]
    public async Task SubmitJoinRequest_WhenUnauthorizedAccessExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        const string secretDiagnostic = "DIAGNOSTIC_MARKER_JOIN_UNDERAGE_9941";
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new UnauthorizedAccessException($"Participation denied: {secretDiagnostic}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.SubmitJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Join request unauthorized.", details.Title);
        Assert.Equal("Caller is not authorized to submit a join request for the specified group.", details.Detail);
        Assert.DoesNotContain(secretDiagnostic, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretDiagnostic, serialized);
        Assert.Contains("Caller is not authorized to submit a join request for the specified group.", serialized);
    }

    [Fact]
    public async Task SubmitJoinRequest_WhenInvalidOperationExceptionThrown_ReturnsBadRequestProblemDetails()
    {
        const string secretDiagnostic = "DIAGNOSTIC_MARKER_CAPACITY_LIMIT_7721";
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new InvalidOperationException($"Group has reached maximum member capacity: {secretDiagnostic}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("hobbies", GroupKind.Club);

        var actionResult = await controller.SubmitJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid join request operation.", details.Title);
        Assert.Equal("The join request operation could not be completed in the current group state.", details.Detail);
        Assert.DoesNotContain(secretDiagnostic, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretDiagnostic, serialized);
    }

    [Fact]
    public async Task SubmitJoinRequest_WhenArgumentExceptionContainsSensitiveIdentifier_ConcealsExceptionDetails()
    {
        const string diagnosticMarker = "DIAGNOSTIC_MARKER_INVALID_ARG_3321";
        var privateId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new ArgumentException($"Invalid arguments with secret: {diagnosticMarker} and profile {privateId}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.SubmitJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid join request arguments.", details.Title);
        Assert.Equal("The join request operation could not be completed with the provided arguments.", details.Detail);
        Assert.DoesNotContain(diagnosticMarker, details.Detail);
        Assert.DoesNotContain(privateId.ToString(), details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(diagnosticMarker, serialized);
        Assert.DoesNotContain(privateId.ToString(), serialized);
    }

    [Fact]
    public async Task CancelJoinRequest_WhenUnauthorizedOrInvalidOperation_ConcealsDiagnostics()
    {
        const string secretMarker = "DIAGNOSTIC_MARKER_CANCEL_AUTH_4412";
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new UnauthorizedAccessException($"Cancel denied: {secretMarker}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupCancelJoinRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.CancelJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Cancel join request unauthorized.", details.Title);
        Assert.Equal("Caller is not authorized to cancel a join request for the specified group.", details.Detail);
        Assert.DoesNotContain(secretMarker, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretMarker, serialized);
    }

    [Fact]
    public async Task ProcessAdmission_WhenUnauthorizedAccessExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        const string secretDiagnostic = "DIAGNOSTIC_MARKER_MODERATION_ROLE_8831";
        var applicantId = Guid.NewGuid();
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new UnauthorizedAccessException($"Actor does not possess the required moderation role: {secretDiagnostic} for {applicantId}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, applicantId, true);

        var actionResult = await controller.ProcessAdmission(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Admission processing unauthorized.", details.Title);
        Assert.Equal("Caller is not authorized to process admissions for the specified group.", details.Detail);
        Assert.DoesNotContain(secretDiagnostic, details.Detail);
        Assert.DoesNotContain(applicantId.ToString(), details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretDiagnostic, serialized);
        Assert.DoesNotContain(applicantId.ToString(), serialized);
    }

    [Fact]
    public async Task LeaveGroup_WhenUnauthorizedOrInvalidOperation_ConcealsDiagnostics()
    {
        const string secretDiagnostic = "DIAGNOSTIC_MARKER_LEAVE_AUTH_1123";
        var service = new StubGroupMembershipOperationService
        {
            ExceptionToThrow = new UnauthorizedAccessException($"Leave denied: {secretDiagnostic}")
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupDepartureRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.LeaveGroup(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Leave group unauthorized.", details.Title);
        Assert.Equal("Caller is not authorized to leave the specified group.", details.Detail);
        Assert.DoesNotContain(secretDiagnostic, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretDiagnostic, serialized);
    }

    [Fact]
    public async Task SubmitJoinRequest_TransactionFailed_ReturnsOkWithFailedResult()
    {
        var service = new StubGroupMembershipOperationService
        {
            SubmitResult = GroupJoinOperationResult.Failed()
        };
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        var actionResult = await controller.SubmitJoinRequest(Guid.NewGuid(), request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<GroupJoinOperationResponseDto>(okResult.Value);

        Assert.False(dto.Success);
        Assert.False(dto.MembershipCreated);
        Assert.False(dto.EntityRemoved);
    }

    #endregion

    #region 8. Cancellation and Response Sanitization

    [Fact]
    public async Task SubmitJoinRequest_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupJoinSubmissionRequest("learning", GroupKind.Classroom);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.SubmitJoinRequest(Guid.NewGuid(), request, cts.Token));
    }

    [Fact]
    public async Task CancelJoinRequest_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupCancelJoinRequest("learning", GroupKind.Classroom);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.CancelJoinRequest(Guid.NewGuid(), request, cts.Token));
    }

    [Fact]
    public async Task ProcessAdmission_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupProcessAdmissionRequest("learning", GroupKind.Classroom, Guid.NewGuid(), true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.ProcessAdmission(Guid.NewGuid(), request, cts.Token));
    }

    [Fact]
    public async Task LeaveGroup_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubGroupMembershipOperationService();
        var controller = new GroupMembershipController(service);
        var request = new GroupDepartureRequest("learning", GroupKind.Classroom);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.LeaveGroup(Guid.NewGuid(), request, cts.Token));
    }

    [Fact]
    public void GroupJoinOperationResponseDto_DoesNotExposePrivateOrDiagnosticFields()
    {
        var propertyNames = typeof(GroupJoinOperationResponseDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain("ProfileId", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApplicantProfileId", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserId", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountId", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diagnostics", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sql", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", propertyNames, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Helper Stubs

    private sealed class StubGroupMembershipOperationService : IGroupMembershipOperationService
    {
        public GroupJoinOperationResult SubmitResult { get; set; } = GroupJoinOperationResult.RequestSubmitted();
        public GroupJoinOperationResult CancelResult { get; set; } = GroupJoinOperationResult.RequestCancelled();
        public GroupJoinOperationResult ProcessResult { get; set; } = GroupJoinOperationResult.Approved();
        public GroupJoinOperationResult LeaveResult { get; set; } = GroupJoinOperationResult.Left();

        public Exception? ExceptionToThrow { get; set; }

        public Guid LastGroupId { get; private set; }
        public GroupModuleScope? LastScope { get; private set; }
        public Guid LastApplicantProfileId { get; private set; }
        public bool? LastApprove { get; private set; }
        public int CallCount { get; private set; }

        public Task<GroupJoinOperationResult> SubmitJoinRequestStrictAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastGroupId = groupId;
            LastScope = scope;

            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            return Task.FromResult(SubmitResult);
        }

        public Task<GroupJoinOperationResult> CancelJoinRequestStrictAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastGroupId = groupId;
            LastScope = scope;

            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            return Task.FromResult(CancelResult);
        }

        public Task<GroupJoinOperationResult> ProcessJoinRequestStrictAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid applicantProfileId,
            bool approve,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastGroupId = groupId;
            LastScope = scope;
            LastApplicantProfileId = applicantProfileId;
            LastApprove = approve;

            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            return Task.FromResult(ProcessResult);
        }

        public Task<GroupJoinOperationResult> LeaveGroupStrictAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastGroupId = groupId;
            LastScope = scope;

            if (ExceptionToThrow is not null) throw ExceptionToThrow;
            return Task.FromResult(LeaveResult);
        }

        public Task<GroupJoinOperationResult> SubmitJoinRequestAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, int callerAge = 25, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<GroupJoinOperationResult> CancelJoinRequestAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<GroupJoinOperationResult> ProcessJoinRequestAsync(Guid groupId, GroupModuleScope scope, string actorModule, Guid actorProfileId, bool isActorAuthorizedRole, Guid applicantProfileId, bool approve, int applicantAge = 25, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<GroupJoinOperationResult> LeaveGroupAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    #endregion
}
