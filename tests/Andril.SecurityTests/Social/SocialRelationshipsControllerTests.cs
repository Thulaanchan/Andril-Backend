using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Controllers.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Social;

public sealed class SocialRelationshipsControllerTests
{
    #region 1. Metadata and Architectural Security Verification

    [Fact]
    public void Controller_HasNonControllerAttribute()
    {
        var nonControllerAttr = typeof(SocialRelationshipsController)
            .GetCustomAttribute<NonControllerAttribute>(inherit: false);

        Assert.NotNull(nonControllerAttr);
    }

    [Fact]
    public void Controller_HasApiControllerAttribute()
    {
        var apiControllerAttr = typeof(SocialRelationshipsController)
            .GetCustomAttribute<ApiControllerAttribute>(inherit: false);

        Assert.NotNull(apiControllerAttr);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var authorizeAttr = typeof(SocialRelationshipsController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        Assert.NotNull(authorizeAttr);
    }

    [Fact]
    public void Controller_HasRouteAttribute_WithCorrectTemplate()
    {
        var routeAttr = typeof(SocialRelationshipsController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);

        Assert.NotNull(routeAttr);
        Assert.Equal("api/v1/social/relationships", routeAttr.Template);
    }

    [Theory]
    [InlineData(nameof(SocialRelationshipsController.RecordInterest))]
    [InlineData(nameof(SocialRelationshipsController.GetRelationshipStatus))]
    [InlineData(nameof(SocialRelationshipsController.RevokeMatch))]
    public void Endpoints_HaveResponseCacheNoStoreAttribute(string methodName)
    {
        var method = typeof(SocialRelationshipsController).GetMethod(methodName);
        Assert.NotNull(method);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>(inherit: false);
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public void DefaultHost_DoesNotActivateSocialRelationshipsController()
    {
        var isNonController = typeof(SocialRelationshipsController).IsDefined(typeof(NonControllerAttribute), inherit: false);
        Assert.True(isNonController, "SocialRelationshipsController must be marked [NonController] to prevent runtime activation.");
    }

    #endregion

    #region 2. Parameter Validation and Invalid Request Handling

    [Fact]
    public void Constructor_NullAccessService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SocialRelationshipsController(null!));
    }

    [Fact]
    public async Task RecordInterest_NullRequest_ReturnsBadRequest()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        var result = await controller.RecordInterest(null!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task RecordInterest_EmptyTargetProfileId_ReturnsBadRequest()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);
        var request = new SocialRecordInterestRequest(Guid.Empty, true);

        var result = await controller.RecordInterest(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task GetRelationshipStatus_EmptyTargetProfileId_ReturnsBadRequest()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        var result = await controller.GetRelationshipStatus(Guid.Empty, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    [Fact]
    public async Task RevokeMatch_EmptyTargetProfileId_ReturnsBadRequest()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        var result = await controller.RevokeMatch(Guid.Empty, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
    }

    #endregion

    #region 3. Interest Recording and Unilateral-Interest Privacy

    [Fact]
    public async Task RecordInterest_PositiveInterestNoMatch_ReturnsOkWithUnilateralInterestConcealed()
    {
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            RecordInterestResult = SocialRelationshipOperationResult.InterestRecorded(SocialRelationshipState.PendingUnilateral)
        };
        var controller = new SocialRelationshipsController(service);
        var request = new SocialRecordInterestRequest(targetId, true);

        var actionResult = await controller.RecordInterest(request, CancellationToken.None);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(targetId, service.LastTargetProfileId);
        Assert.True(service.LastIsPositive);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRelationshipOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MatchCreated);
        Assert.Null(dto.MatchId);
        Assert.Equal("PendingUnilateral", dto.VisibleState);
    }

    [Fact]
    public async Task RecordInterest_MutualMatchEstablished_ReturnsOkWithMatchDetails()
    {
        var targetId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            RecordInterestResult = SocialRelationshipOperationResult.MatchEstablished(matchId)
        };
        var controller = new SocialRelationshipsController(service);
        var request = new SocialRecordInterestRequest(targetId, true);

        var actionResult = await controller.RecordInterest(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRelationshipOperationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.True(dto.MatchCreated);
        Assert.Equal(matchId, dto.MatchId);
        Assert.Equal("Matched", dto.VisibleState);
    }

    #endregion

    #region 4. Authorized Mutual-Match Inspection

    [Fact]
    public async Task GetRelationshipStatus_WhenMatched_ReturnsOkWithActiveMatchDetails()
    {
        var targetId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var matchedAt = DateTime.UtcNow;

        var service = new StubSocialRelationshipAccessService
        {
            StatusResult = SocialRelationshipStatusResult.Matched(matchId, matchedAt)
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.GetRelationshipStatus(targetId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRelationshipStatusResponseDto>(okResult.Value);

        Assert.True(dto.HasActiveMatch);
        Assert.Equal(matchId, dto.MatchId);
        Assert.Equal(matchedAt, dto.MatchedAtUtc);
    }

    [Fact]
    public async Task GetRelationshipStatus_WhenNotMatched_ReturnsOkWithNotMatchedConcealingDecisions()
    {
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            StatusResult = SocialRelationshipStatusResult.NotMatched()
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.GetRelationshipStatus(targetId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRelationshipStatusResponseDto>(okResult.Value);

        Assert.False(dto.HasActiveMatch);
        Assert.Null(dto.MatchId);
        Assert.Null(dto.MatchedAtUtc);
    }

    #endregion

    #region 5. Match Revocation (Unmatch) Behavior

    [Fact]
    public async Task RevokeMatch_ExistingMatch_ReturnsOkWithMatchRevokedTrue()
    {
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            RevocationResult = new SocialRevocationOperationResult(success: true, matchRevoked: true)
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.RevokeMatch(targetId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRevocationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.True(dto.MatchRevoked);
    }

    [Fact]
    public async Task RevokeMatch_NoExistingMatch_ReturnsOkWithMatchRevokedFalse()
    {
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            RevocationResult = new SocialRevocationOperationResult(success: true, matchRevoked: false)
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.RevokeMatch(targetId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialRevocationResponseDto>(okResult.Value);

        Assert.True(dto.Success);
        Assert.False(dto.MatchRevoked);
    }

    #endregion

    #region 6. Authorization, Blocking, and Transaction Failure Handling

    [Fact]
    public async Task RecordInterest_WhenProfileAccessDeniedExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new ProfileAccessDeniedException()
        };
        var controller = new SocialRelationshipsController(service);
        var request = new SocialRecordInterestRequest(Guid.NewGuid(), true);

        var actionResult = await controller.RecordInterest(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Profile access denied.", details.Title);
        Assert.Equal("Caller is not authorized to perform the requested relationship action.", details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("ProfileAccessDeniedException", serialized);
        Assert.Contains("Caller is not authorized to perform the requested relationship action.", serialized);
    }

    [Fact]
    public async Task GetRelationshipStatus_WhenProfileAccessDeniedExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new ProfileAccessDeniedException()
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.GetRelationshipStatus(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Profile access denied.", details.Title);
        Assert.Equal("Caller is not authorized to access relationship status.", details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("ProfileAccessDeniedException", serialized);
        Assert.Contains("Caller is not authorized to access relationship status.", serialized);
    }

    [Fact]
    public async Task RevokeMatch_WhenProfileAccessDeniedExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new ProfileAccessDeniedException()
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.RevokeMatch(Guid.NewGuid(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Profile access denied.", details.Title);
        Assert.Equal("Caller is not authorized to revoke the specified relationship.", details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("ProfileAccessDeniedException", serialized);
        Assert.Contains("Caller is not authorized to revoke the specified relationship.", serialized);
    }

    [Fact]
    public async Task RecordInterest_WhenArgumentExceptionContainsSensitiveDiagnostics_ConcealsExceptionDetails()
    {
        const string secretMarker = "DIAGNOSTIC_MARKER_RELATIONSHIP_INTEREST_9812";
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new ArgumentException($"Failed with private token: {secretMarker} for target {targetId}")
        };
        var controller = new SocialRelationshipsController(service);
        var request = new SocialRecordInterestRequest(targetId, true);

        var actionResult = await controller.RecordInterest(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid interest operation.", details.Title);
        Assert.Equal("The relationship interest operation could not be completed with the provided parameters.", details.Detail);
        Assert.DoesNotContain(secretMarker, details.Detail);
        Assert.DoesNotContain(targetId.ToString(), details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretMarker, serialized);
        Assert.DoesNotContain(targetId.ToString(), serialized);
    }

    [Fact]
    public async Task GetRelationshipStatus_WhenArgumentExceptionContainsSensitiveDiagnostics_ConcealsExceptionDetails()
    {
        const string secretMarker = "DIAGNOSTIC_MARKER_RELATIONSHIP_STATUS_7712";
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new ArgumentException($"Failed status query with private token: {secretMarker}")
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.GetRelationshipStatus(targetId, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid status operation.", details.Title);
        Assert.Equal("The relationship status operation could not be completed with the provided parameters.", details.Detail);
        Assert.DoesNotContain(secretMarker, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretMarker, serialized);
    }

    [Fact]
    public async Task RevokeMatch_WhenArgumentOrInvalidOperationExceptionThrown_ConcealsTransactionDiagnostics()
    {
        const string secretMarker = "TRANSACTION_CONFLICT_SECRET_5541";
        var targetId = Guid.NewGuid();
        var service = new StubSocialRelationshipAccessService
        {
            ExceptionToThrow = new InvalidOperationException($"Revocation transaction failed: {secretMarker}")
        };
        var controller = new SocialRelationshipsController(service);

        var actionResult = await controller.RevokeMatch(targetId, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid revocation operation.", details.Title);
        Assert.Equal("The relationship revocation operation could not be completed due to a conflict or invalid state.", details.Detail);
        Assert.DoesNotContain(secretMarker, details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(secretMarker, serialized);
    }

    #endregion

    #region 7. Cancellation and Information Concealment

    [Fact]
    public async Task RecordInterest_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.RecordInterest(new SocialRecordInterestRequest(Guid.NewGuid(), true), cts.Token));
    }

    [Fact]
    public async Task GetRelationshipStatus_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetRelationshipStatus(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task RevokeMatch_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubSocialRelationshipAccessService();
        var controller = new SocialRelationshipsController(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.RevokeMatch(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public void ResponseDtos_DoNotExposeInternalDiagnosticsOrSensitiveFields()
    {
        var dtoTypes = new[]
        {
            typeof(SocialRelationshipOperationResponseDto),
            typeof(SocialRelationshipStatusResponseDto),
            typeof(SocialRevocationResponseDto)
        };

        foreach (var type in dtoTypes)
        {
            var propertyNames = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToArray();

            Assert.DoesNotContain("CallerProfileId", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProfileId", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("UserId", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("AccountId", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Diagnostics", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sql", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Connection", propertyNames, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("StackTrace", propertyNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Helper Stubs

    private sealed class StubSocialRelationshipAccessService : ISocialRelationshipAccessService
    {
        public SocialRelationshipOperationResult RecordInterestResult { get; set; } =
            SocialRelationshipOperationResult.InterestRecorded(SocialRelationshipState.None);

        public SocialRelationshipStatusResult StatusResult { get; set; } =
            SocialRelationshipStatusResult.NotMatched();

        public SocialRevocationOperationResult RevocationResult { get; set; } =
            new(success: true, matchRevoked: true);

        public Exception? ExceptionToThrow { get; set; }

        public Guid LastTargetProfileId { get; private set; }
        public bool? LastIsPositive { get; private set; }
        public int CallCount { get; private set; }

        public Task<SocialRelationshipOperationResult> RecordInterestAsync(
            Guid targetProfileId,
            bool isPositive,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastTargetProfileId = targetProfileId;
            LastIsPositive = isPositive;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(RecordInterestResult);
        }

        public Task<SocialRelationshipStatusResult> GetRelationshipStatusAsync(
            Guid targetProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastTargetProfileId = targetProfileId;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(StatusResult);
        }

        public Task<SocialRevocationOperationResult> RevokeMatchAsync(
            Guid targetProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastTargetProfileId = targetProfileId;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(RevocationResult);
        }
    }

    #endregion
}
