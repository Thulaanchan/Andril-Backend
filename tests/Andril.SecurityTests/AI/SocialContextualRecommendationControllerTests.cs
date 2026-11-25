using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Recommendations;
using Andril.AI.Services;
using Andril.API.Controllers.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>
/// Verifies architectural constraints, trusted caller authorization, module isolation,
/// consent privacy preservation, and fail-closed security for <see cref="SocialContextualRecommendationController"/> (CF-18).
/// </summary>
/// <remarks>
/// Authoritative Acceptance and Non-Relational Boundary Note:
/// Unit and security tests verify dormant MVC exclusion and in-memory authorization contracts.
/// They do NOT constitute relational acceptance, physical database verification, or durable exactly-once replay prevention.
/// Authoritative single-use dispatch, quota verification, and completion fencing remain outstanding acceptance requirements
/// subject to durable transaction repository implementation and separate production authorization.
/// </remarks>
public sealed class SocialContextualRecommendationControllerTests
{
    private static readonly DateTime TestNow = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static Guid Id(int b) => new(b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    #region 1. Metadata and Architectural Security Attributes

    [Fact]
    public void Controller_HasNonControllerAttribute()
    {
        var attr = typeof(SocialContextualRecommendationController)
            .GetCustomAttribute<NonControllerAttribute>(inherit: false);

        Assert.NotNull(attr);
    }

    [Fact]
    public void DomainOverload_HasNonActionAttribute()
    {
        var method = typeof(SocialContextualRecommendationController)
            .GetMethod(nameof(SocialContextualRecommendationController.GetRecommendationAsync),
                [typeof(AiConsentPendingReference), typeof(CancellationToken)]);

        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<NonActionAttribute>(inherit: false);
        Assert.NotNull(attr);
    }

    [Fact]
    public void HttpOverload_HasNonActionAttribute()
    {
        var method = typeof(SocialContextualRecommendationController)
            .GetMethod(nameof(SocialContextualRecommendationController.GetRecommendationAsync),
                [typeof(SocialContextualRecommendationRequest), typeof(CancellationToken)]);

        Assert.NotNull(method);
        var attr = method.GetCustomAttribute<NonActionAttribute>(inherit: false);
        Assert.NotNull(attr);
    }

    [Fact]
    public void MvcFeatureDiscovery_ExcludesSocialContextualRecommendationController()
    {
        var services = new ServiceCollection();
        var mvc = services.AddControllers().AddApplicationPart(typeof(SocialContextualRecommendationController).Assembly);
        var feature = new ControllerFeature();
        mvc.PartManager.PopulateFeature(feature);

        Assert.NotEmpty(feature.Controllers);
        Assert.DoesNotContain(feature.Controllers, t => t.AsType() == typeof(SocialContextualRecommendationController));
    }

    [Fact]
    public void MvcActionDescriptors_DoNotExposeSocialContextualRecommendationController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(SocialContextualRecommendationController).Assembly);
        using var provider = services.BuildServiceProvider();

        var actionDescriptors = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        Assert.DoesNotContain(actionDescriptors,
            a => a is ControllerActionDescriptor c && c.ControllerTypeInfo.AsType() == typeof(SocialContextualRecommendationController));
    }

    [Fact]
    public void Controller_HasApiControllerAttribute()
    {
        var attr = typeof(SocialContextualRecommendationController)
            .GetCustomAttribute<ApiControllerAttribute>(inherit: false);

        Assert.NotNull(attr);
    }

    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var attr = typeof(SocialContextualRecommendationController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        Assert.NotNull(attr);
    }

    [Fact]
    public void Controller_HasRouteAttribute_WithCorrectTemplate()
    {
        var attr = typeof(SocialContextualRecommendationController)
            .GetCustomAttribute<RouteAttribute>(inherit: false);

        Assert.NotNull(attr);
        Assert.Equal("api/v1/social/ai/recommendations", attr.Template);
    }

    [Fact]
    public void Controller_HasResponseCacheNoStoreAttribute()
    {
        var attr = typeof(SocialContextualRecommendationController)
            .GetCustomAttribute<ResponseCacheAttribute>(inherit: false);

        Assert.NotNull(attr);
        Assert.True(attr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, attr.Location);
    }

    [Fact]
    public void DefaultHost_DoesNotActivateSocialContextualRecommendationController()
    {
        var isNonController = typeof(SocialContextualRecommendationController)
            .IsDefined(typeof(NonControllerAttribute), inherit: false);

        Assert.True(isNonController, "SocialContextualRecommendationController must be marked [NonController] to prevent runtime activation.");
    }

    #endregion

    #region 2. Constructor Parameter Validation

    [Fact]
    public void Constructor_NullAuthorization_ThrowsArgumentNullException()
    {
        var (_, _, _, _, _, service) = CreateHarness();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SocialContextualRecommendationController(null!, service));

        Assert.Equal("authorization", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullRecommendationService_ThrowsArgumentNullException()
    {
        var (auth, _, _, _, _, _) = CreateHarness();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SocialContextualRecommendationController(auth, null!));

        Assert.Equal("recommendationService", ex.ParamName);
    }

    [Fact]
    public void Constructor_OptionalConsentServiceNull_Succeeds()
    {
        var (auth, _, _, _, _, service) = CreateHarness();

        var controller = new SocialContextualRecommendationController(auth, service, consentService: null);

        Assert.NotNull(controller);
    }

    #endregion

    #region 3. Request Bounds and Payload Validation

    [Fact]
    public async Task GetRecommendationAsync_NullRequest_ThrowsArgumentNullException()
    {
        var controller = CreateController();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            controller.GetRecommendationAsync((SocialContextualRecommendationRequest)null!));
    }

    [Fact]
    public async Task GetRecommendationAsync_NullPendingConsent_ThrowsArgumentNullException()
    {
        var controller = CreateController();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            controller.GetRecommendationAsync((AiConsentPendingReference)null!));
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptyConversationId_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { ConversationId = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("ConversationId", ex.Message);
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptyOperationId_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { OperationId = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("OperationId", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetRecommendationAsync_InvalidPurposeId_ThrowsArgumentException(string? purposeId)
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { PurposeId = purposeId! };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("PurposeId", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetRecommendationAsync_InvalidPurposeVersion_ThrowsArgumentException(string? version)
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { PurposeVersion = version! };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("PurposeVersion", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetRecommendationAsync_InvalidScopeId_ThrowsArgumentException(string? scopeId)
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { ScopeId = scopeId! };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("ScopeId", ex.Message);
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptySessionEpoch_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { SessionEpoch = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("SessionEpoch", ex.Message);
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptyModuleEpoch_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { ModuleEpoch = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("ModuleEpoch", ex.Message);
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptyConsentRevision_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { ConsentRevision = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("ConsentRevision", ex.Message);
    }

    [Fact]
    public async Task GetRecommendationAsync_EmptyAttemptId_ThrowsArgumentException()
    {
        var controller = CreateController();
        var request = CreateValidRequest() with { AttemptId = Guid.Empty };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.GetRecommendationAsync(request));

        Assert.Contains("AttemptId", ex.Message);
    }

    #endregion

    #region 4. Caller Authorization and Module Isolation

    [Fact]
    public async Task GetRecommendationAsync_NullAuthorizedActor_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness();
        auth.Actor = null;
        var controller = new SocialContextualRecommendationController(auth, service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(CreateValidRequest()));
    }

    [Fact]
    public async Task GetRecommendationAsync_InvalidAuthorizedActor_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness();
        auth.Actor = new AiAuthorizedActor(Guid.Empty, Id(2), AiModule.Social);
        var controller = new SocialContextualRecommendationController(auth, service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(CreateValidRequest()));
    }

    [Fact]
    public async Task GetRecommendationAsync_NonSocialAuthorizedActor_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness(AiModule.Hobbies);
        var controller = new SocialContextualRecommendationController(auth, service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(CreateValidRequest()));
    }

    [Fact]
    public async Task GetRecommendationAsync_Domain_ForeignActor_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness(AiModule.Social);
        var controller = new SocialContextualRecommendationController(auth, service);

        var foreignActor = new AiAuthorizedActor(Id(99), Id(98), AiModule.Social);
        var foreignPending = CreatePendingConsent(foreignActor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(foreignPending));
    }

    [Fact]
    public async Task GetRecommendationAsync_Domain_CrossModuleActor_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness(AiModule.Social);
        var controller = new SocialContextualRecommendationController(auth, service);

        var crossModuleActor = new AiAuthorizedActor(Id(1), Id(2), AiModule.Learning);
        var crossPending = CreatePendingConsent(crossModuleActor);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(crossPending));
    }

    [Fact]
    public async Task GetRecommendationAsync_ActorShiftDuringProcessing_ThrowsUnauthorizedAccessException()
    {
        var (auth, _, _, _, _, service) = CreateHarness(AiModule.Social);
        var controller = new SocialContextualRecommendationController(auth, service);

        // When generator runs, shift the authorized actor identity
        var generator = (StubRecommendationGenerator)typeof(AiContextualRecommendationService)
            .GetField("_generator", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

        generator.OnGenerate = () =>
        {
            auth.Actor = new AiAuthorizedActor(Id(77), Id(78), AiModule.Social);
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(CreateValidRequest()));
    }

    #endregion

    #region 5. Recommendation Delivery and Privacy Preservation

    [Fact]
    public async Task GetRecommendationAsync_AuthorizedExplicitConsent_DeliversRecommendationContent()
    {
        var (auth, _, _, repo, gen, service) = CreateHarness(AiModule.Social);
        gen.Content = "Recommended: Connect with Jane who shares your interest in Photography";

        var controller = new SocialContextualRecommendationController(auth, service);
        var request = CreateValidRequest();

        var result = await controller.GetRecommendationAsync(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SocialContextualRecommendationResponse>(okResult.Value);

        Assert.True(response.Delivered);
        Assert.True(response.IsDelivered);
        Assert.Equal("Recommended: Connect with Jane who shares your interest in Photography", response.Content);
        Assert.True(gen.GeneratorCalled);
    }

    [Fact]
    public async Task GetRecommendationAsync_Domain_DeliversRecommendationContent()
    {
        var (auth, _, _, repo, gen, service) = CreateHarness(AiModule.Social);
        gen.Content = "Recommended: Join the Book Club circle";

        var controller = new SocialContextualRecommendationController(auth, service);
        var pending = CreatePendingConsent(auth.Actor!);

        var result = await controller.GetRecommendationAsync(pending);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SocialContextualRecommendationResponse>(okResult.Value);

        Assert.True(response.Delivered);
        Assert.True(response.IsDelivered);
        Assert.Equal("Recommended: Join the Book Club circle", response.Content);
    }

    [Fact]
    public async Task GetRecommendationAsync_SuppressedDelivery_ConcealsContentAndSuppressionReason()
    {
        var (auth, _, _, repo, gen, service) = CreateHarness(AiModule.Social);
        // Explicit consent continuation is denied
        repo.Result = new AiConsentResult(AiConsentOutcome.Denied, null, null);
        gen.Content = "Sensitive generated data that must NOT leak";

        var controller = new SocialContextualRecommendationController(auth, service);
        var request = CreateValidRequest();

        var result = await controller.GetRecommendationAsync(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SocialContextualRecommendationResponse>(okResult.Value);

        Assert.False(response.Delivered);
        Assert.False(response.IsDelivered);
        Assert.Null(response.Content);
        Assert.False(gen.GeneratorCalled, "Generator should never run when consent continuation is not verified");
    }

    [Fact]
    public async Task GetRecommendationAsync_ConsentExpired_SuppressesAndConcealsContent()
    {
        var (auth, _, _, repo, gen, service) = CreateHarness(AiModule.Social);
        repo.Result = new AiConsentResult(AiConsentOutcome.Expired, null, null);

        var controller = new SocialContextualRecommendationController(auth, service);
        var request = CreateValidRequest();

        var result = await controller.GetRecommendationAsync(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SocialContextualRecommendationResponse>(okResult.Value);

        Assert.False(response.Delivered);
        Assert.False(response.IsDelivered);
        Assert.Null(response.Content);
    }

    [Fact]
    public async Task GetRecommendationAsync_PrivacyGateDenied_ThrowsUnauthorizedAccessException()
    {
        var (auth, gate, _, _, _, service) = CreateHarness(AiModule.Social);
        gate.Outcome = AiPrivacyNoticeGateOutcome.Denied;

        var controller = new SocialContextualRecommendationController(auth, service);
        var request = CreateValidRequest();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            controller.GetRecommendationAsync(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetRecommendationAsync_DeliveredWithEmptyContent_ProjectsNonDeliveredResponse(string emptyContent)
    {
        var (auth, _, _, repo, gen, service) = CreateHarness(AiModule.Social);
        gen.Content = emptyContent;

        var controller = new SocialContextualRecommendationController(auth, service);
        var request = CreateValidRequest();

        var result = await controller.GetRecommendationAsync(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SocialContextualRecommendationResponse>(okResult.Value);

        Assert.False(response.Delivered);
        Assert.False(response.IsDelivered);
        Assert.Null(response.Content);
    }

    #endregion

    #region 6. Cancellation Token Propagation

    [Fact]
    public async Task GetRecommendationAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var controller = CreateController();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetRecommendationAsync(CreateValidRequest(), cts.Token));
    }

    [Fact]
    public async Task GetRecommendationAsync_Domain_CancelledToken_ThrowsOperationCanceledException()
    {
        var (auth, _, _, _, _, service) = CreateHarness(AiModule.Social);
        var controller = new SocialContextualRecommendationController(auth, service);
        var pending = CreatePendingConsent(auth.Actor!);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetRecommendationAsync(pending, cts.Token));
    }

    #endregion

    #region Test Fixtures and Helpers

    private static SocialContextualRecommendationRequest CreateValidRequest() =>
        new(
            ConversationId: Id(3),
            OperationId: Id(4),
            PurposeId: "social-recommendations",
            PurposeVersion: "v1",
            ScopeId: "mutual-interests",
            SessionEpoch: Id(5),
            ModuleEpoch: Id(6),
            ConsentRevision: Id(7),
            AttemptId: Id(8));

    private static AiConsentPendingReference CreatePendingConsent(AiAuthorizedActor actor)
    {
        var op = new AiOperationReference(actor, Id(3), Id(4));
        var purpose = new AiConsentPurpose("social-recommendations", "v1", "mutual-interests");
        var consentRef = new AiConsentReference(op, purpose, Id(5), Id(6));
        return new AiConsentPendingReference(consentRef, Id(7), Id(8));
    }

    private static AiConsentSnapshot CreateSnapshot(AiConsentState state, Guid attemptId, Guid revision, AiAuthorizedActor actor)
    {
        var op = new AiOperationReference(actor, Id(3), Id(4));
        var purpose = new AiConsentPurpose("social-recommendations", "v1", "mutual-interests");
        var consentRef = new AiConsentReference(op, purpose, Id(5), Id(6));
        return new AiConsentSnapshot(
            consentRef,
            new AiConsentNotice("v1", "Social recommendations", "Contextual suggestions for connections"),
            state,
            revision,
            TestNow,
            state is AiConsentState.GrantedUnused or AiConsentState.Started or AiConsentState.Expired ? TestNow : null,
            state == AiConsentState.Started ? TestNow : null,
            state == AiConsentState.Started ? attemptId : null);
    }

    private static SocialContextualRecommendationController CreateController(AiModule module = AiModule.Social)
    {
        var (auth, _, _, _, _, service) = CreateHarness(module);
        return new SocialContextualRecommendationController(auth, service);
    }

    private static (
        StubActorAuthorization Auth,
        StubPrivacyNoticeGate Gate,
        AiPrivacyNoticeGuard Guard,
        StubConsentRepository Repo,
        StubRecommendationGenerator Generator,
        AiContextualRecommendationService Service)
        CreateHarness(AiModule module = AiModule.Social)
    {
        var actor = new AiAuthorizedActor(Id(1), Id(2), module);
        var auth = new StubActorAuthorization { Actor = actor };
        var gate = new StubPrivacyNoticeGate();
        var guard = new AiPrivacyNoticeGuard(auth, gate);

        var op = new AiOperationReference(actor, Id(3), Id(4));
        var lifecycle = new AiConsentLifecycle(op, Id(5), Id(6), TestNow, true, true, true, true);
        var repo = new StubConsentRepository
        {
            Lifecycle = lifecycle,
            Result = new AiConsentResult(
                AiConsentOutcome.ContinuationVerified,
                CreateSnapshot(AiConsentState.Started, Id(8), Id(7), actor),
                lifecycle)
        };

        var consentService = new AiContextualConsentService(auth, guard, repo);
        var generator = new StubRecommendationGenerator();
        var service = new AiContextualRecommendationService(auth, guard, consentService, generator);

        return (auth, gate, guard, repo, generator, service);
    }

    private sealed class StubActorAuthorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }

        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default) =>
            Task.FromResult(Actor);
    }

    private sealed class StubPrivacyNoticeGate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.Satisfied;

        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome);
    }

    private sealed class StubConsentRepository : IAiContextualConsentRepository
    {
        public AiConsentLifecycle? Lifecycle { get; set; }
        public AiConsentResult Result { get; set; } = null!;

        public Task<AiConsentLifecycle?> ReadLifecycleAsync(AiOperationReference operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lifecycle);

        public Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubRecommendationGenerator : IAiContextualRecommendationGenerator
    {
        public string Content { get; set; } = "Recommended: Connect with common interests";
        public bool GeneratorCalled { get; private set; }
        public Action? OnGenerate { get; set; }

        public Task<string> GenerateRecommendationAsync(AiModule module, AiAuthorizedActor actor, AiConsentPurpose purpose, CancellationToken cancellationToken = default)
        {
            GeneratorCalled = true;
            OnGenerate?.Invoke();
            return Task.FromResult(Content);
        }
    }

    #endregion
}
