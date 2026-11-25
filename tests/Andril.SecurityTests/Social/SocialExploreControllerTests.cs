using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Controllers.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Social;

public sealed class SocialExploreControllerTests
{
    #region 1. Metadata and Architectural Security Verification

    [Fact]
    public void Controller_HasNonControllerAttribute()
    {
        var nonControllerAttr = typeof(SocialExploreController)
            .GetCustomAttribute<NonControllerAttribute>(inherit: false);

        Assert.NotNull(nonControllerAttr);
    }

    [Fact]
    public void Controller_HasApiControllerAttribute()
    {
        var apiControllerAttr = typeof(SocialExploreController)
            .GetCustomAttribute<ApiControllerAttribute>(inherit: false);

        Assert.NotNull(apiControllerAttr);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var authorizeAttr = typeof(SocialExploreController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        Assert.NotNull(authorizeAttr);
    }

    [Fact]
    public void Controller_HasRouteAttribute_WithCorrectTemplate()
    {
        var routeAttr = typeof(SocialExploreController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);

        Assert.NotNull(routeAttr);
        Assert.Equal("api/v1/social/explore", routeAttr.Template);
    }

    [Fact]
    public void GetExploreFeed_HasResponseCacheNoStoreAttribute()
    {
        var method = typeof(SocialExploreController).GetMethod(nameof(SocialExploreController.GetExploreFeed));
        Assert.NotNull(method);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>(inherit: false);
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public void DefaultHost_DoesNotActivateSocialExploreController()
    {
        // Controllers decorated with [NonController] are explicitly excluded from automatic MVC discovery
        var isNonController = typeof(SocialExploreController).IsDefined(typeof(NonControllerAttribute), inherit: false);
        Assert.True(isNonController, "SocialExploreController must be marked [NonController] to prevent runtime activation.");
    }

    #endregion

    #region 2. Request Validation and Bounds Enforcement

    [Fact]
    public void Constructor_NullAccessService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SocialExploreController(null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task GetExploreFeed_NegativeRotationOffset_ReturnsBadRequest(int offset)
    {
        var service = new StubSocialDiscoveryAccessService();
        var controller = new SocialExploreController(service);

        var result = await controller.GetExploreFeed(rotationOffset: offset, limit: 20);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid rotation offset.", details.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public async Task GetExploreFeed_ZeroOrNegativeLimit_ReturnsBadRequest(int limit)
    {
        var service = new StubSocialDiscoveryAccessService();
        var controller = new SocialExploreController(service);

        var result = await controller.GetExploreFeed(rotationOffset: 0, limit: limit);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid candidate limit.", details.Title);
    }

    [Fact]
    public async Task GetExploreFeed_CancellationRequested_ThrowsOperationCanceledException()
    {
        var service = new StubSocialDiscoveryAccessService();
        var controller = new SocialExploreController(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetExploreFeed(cancellationToken: cts.Token));
    }

    #endregion

    #region 3. Delegation and Result Projection

    [Fact]
    public async Task GetExploreFeed_ValidRequest_DelegatesToAccessServiceAndReturnsOkWithPrivacySafeDtos()
    {
        var candId = Guid.NewGuid();
        var eval = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            sharedLanguages: new[] { "English", "Spanish" },
            sharedConnectionGoals: new[] { "Friendship" });

        var card = SocialExploreCandidateCard.Create(
            candId,
            "TargetCandidate",
            25,
            15,
            eval);

        var service = new StubSocialDiscoveryAccessService(new[] { card });
        var controller = new SocialExploreController(service);

        var actionResult = await controller.GetExploreFeed(rotationOffset: 2, limit: 15);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(2, service.LastRotationOffset);
        Assert.Equal(15, service.LastLimit);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<SocialExploreCandidateDto>>(okResult.Value);

        var dto = Assert.Single(dtos);
        Assert.Equal(candId, dto.CandidateProfileId);
        Assert.Equal("TargetCandidate", dto.DisplayName);
        Assert.Equal(25, dto.Age);
        Assert.Equal(15, dto.ApproximateDistanceKm);
        Assert.Equal(new[] { "English", "Spanish" }, dto.SharedLanguages);
        Assert.Equal(new[] { "Friendship" }, dto.SharedConnectionGoals);
    }

    [Fact]
    public async Task GetExploreFeed_EmptyFeed_ReturnsOkWithEmptyList()
    {
        var service = new StubSocialDiscoveryAccessService(Array.Empty<SocialExploreCandidateCard>());
        var controller = new SocialExploreController(service);

        var actionResult = await controller.GetExploreFeed(rotationOffset: 0, limit: 20);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<SocialExploreCandidateDto>>(okResult.Value);
        Assert.Empty(dtos);
    }

    #endregion

    #region 4. Privacy, Information Concealment, and Security Safeguards

    [Fact]
    public void SocialExploreCandidateDto_DoesNotExposePrivateOrInternalFields()
    {
        var properties = typeof(SocialExploreCandidateDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        // Must NOT expose caller identity
        Assert.DoesNotContain("CallerProfileId", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProfileId", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserId", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountId", properties, StringComparer.OrdinalIgnoreCase);

        // Must NOT expose questionnaire answers, personality metrics, or scoring internals
        Assert.DoesNotContain("Answers", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("QuestionnaireAnswers", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("QuestionnaireResponse", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Score", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scores", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Weights", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CompatibilityScore", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diagnostics", properties, StringComparer.OrdinalIgnoreCase);

        // Must NOT expose contact details or precise coordinates
        Assert.DoesNotContain("Email", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Latitude", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Longitude", properties, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetExploreFeed_WhenProfileAccessDeniedExceptionThrown_ReturnsForbiddenProblemDetails()
    {
        var service = StubSocialDiscoveryAccessService.Throwing(new ProfileAccessDeniedException());
        var controller = new SocialExploreController(service);

        var actionResult = await controller.GetExploreFeed();

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Profile access denied.", details.Title);
        Assert.Equal("Caller is not authorized to access Social discovery recommendations.", details.Detail);
    }

    [Fact]
    public async Task GetExploreFeed_WhenProfileAccessDenied_ConcealsInternalTypeDiagnosticsAndReturnsSafeDetail()
    {
        var service = StubSocialDiscoveryAccessService.Throwing(new ProfileAccessDeniedException());
        var controller = new SocialExploreController(service);

        var actionResult = await controller.GetExploreFeed();

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

        var details = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, details.Status);
        Assert.Equal("Profile access denied.", details.Title);
        Assert.Equal("Caller is not authorized to access Social discovery recommendations.", details.Detail);

        // Security / privacy: Exception type name and namespace must NOT leak in Detail
        Assert.DoesNotContain("ProfileAccessDeniedException", details.Detail);
        Assert.DoesNotContain("Andril.Common", details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("ProfileAccessDeniedException", serialized);
        Assert.DoesNotContain("Andril.Common", serialized);
        Assert.Contains("Caller is not authorized to access Social discovery recommendations.", serialized);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task GetExploreFeed_WhenNegativeOffset_ReturnsSafeBadRequestWithoutDiagnostics(int negativeOffset)
    {
        var service = new StubSocialDiscoveryAccessService();
        var controller = new SocialExploreController(service);

        var actionResult = await controller.GetExploreFeed(rotationOffset: negativeOffset);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var details = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal("Invalid rotation offset.", details.Title);
        Assert.Equal("Rotation offset cannot be negative.", details.Detail);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain("Exception", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProblemDetails_SerializesSafelyWithoutInternalDiagnostics()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Profile access denied.",
            Detail = "Caller is not authorized to access Social discovery recommendations."
        };

        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain("Exception", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProfileId", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Caller is not authorized to access Social discovery recommendations.", serialized);
    }

    #endregion

    #region Helper Stubs

    private sealed class StubSocialDiscoveryAccessService : ISocialDiscoveryAccessService
    {
        private readonly IReadOnlyList<SocialExploreCandidateCard>? _cards;
        private readonly Exception? _exceptionToThrow;

        public int LastRotationOffset { get; private set; }
        public int LastLimit { get; private set; }
        public int CallCount { get; private set; }

        public StubSocialDiscoveryAccessService(IReadOnlyList<SocialExploreCandidateCard>? cards = null)
        {
            _cards = cards;
            _exceptionToThrow = null;
        }

        public StubSocialDiscoveryAccessService(Exception exceptionToThrow)
        {
            _cards = null;
            _exceptionToThrow = exceptionToThrow;
        }

        public static StubSocialDiscoveryAccessService Throwing(Exception ex) => new(ex);

        public Task<IReadOnlyList<SocialExploreCandidateCard>> DiscoverCandidatesAsync(
            int rotationOffset = 0,
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRotationOffset = rotationOffset;
            LastLimit = limit;

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }

            return Task.FromResult(_cards ?? Array.Empty<SocialExploreCandidateCard>());
        }
    }

    #endregion
}
