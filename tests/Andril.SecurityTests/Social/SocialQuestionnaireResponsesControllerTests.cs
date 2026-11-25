using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Controllers.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Social;

/// <summary>
/// Security, authorization, privacy, and contract verification tests for
/// <see cref="SocialQuestionnaireResponsesController"/> (SOC-01).
/// </summary>
public sealed class SocialQuestionnaireResponsesControllerTests
{
    #region Controller Discovery and Routing Attributes

    [Fact]
    public void Controller_HasExpectedAttributes_AndIsExcludedFromMvcDiscovery()
    {
        var type = typeof(SocialQuestionnaireResponsesController);

        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());

        // Mandatory guardrail: Excluded from automatic MVC discovery
        var nonControllerAttr = type.GetCustomAttribute<NonControllerAttribute>();
        Assert.NotNull(nonControllerAttr);

        var routeAttr = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("api/v1/social/questionnaires/responses", routeAttr.Template);
    }

    [Fact]
    public void SubmitResponse_HasExpectedActionAttributes()
    {
        var method = typeof(SocialQuestionnaireResponsesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(SocialQuestionnaireResponsesController.SubmitResponse)
                && m.GetParameters().Any(p => p.ParameterType == typeof(JsonElement)));

        Assert.NotNull(method);

        var httpPost = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("", httpPost.Template);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public void GetMyActiveResponse_HasExpectedActionAttributes()
    {
        var method = typeof(SocialQuestionnaireResponsesController)
            .GetMethod(nameof(SocialQuestionnaireResponsesController.GetMyActiveResponse));
        Assert.NotNull(method);

        var httpGet = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("me", httpGet.Template);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public void ProgramSource_DoesNotActivateQuestionnaireResponsesController_NorDeferredPersistence()
    {
        var programSource = ReadProgramSource();

        Assert.DoesNotContain("SocialQuestionnaireResponsesController", programSource);
        Assert.DoesNotContain("AddSocialQuestionnaireResponseServices", programSource);
        Assert.DoesNotContain("AddSocialPersistence", programSource);
    }

    #endregion

    #region Constructor Guardrails

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnaireResponsesController(null!, lifecycle));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnaireResponsesController(orchestrator, null!));
    }

    #endregion

    #region Submission (POST) Verification

    [Fact]
    public async Task SubmitResponse_ValidJsonPayload_DelegatesToOrchestrator_AndReturnsOkWithPrivacySafeDto()
    {
        var expectedResponseId = Guid.NewGuid();
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(expectedResponseId, QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """
        {
            "version": "v1",
            "answers": [
                { "questionId": "q01", "optionId": "1" }
            ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var responseDto = Assert.IsType<SocialQuestionnaireSubmissionResponseDto>(okResult.Value);

        Assert.Equal(expectedResponseId, responseDto.ResponseId);
        Assert.Equal("Completed", responseDto.Status);

        // Verify delegation to orchestrator
        Assert.NotNull(orchestrator.LastSubmission);
        Assert.Equal("v1", orchestrator.LastSubmission.Version);
        Assert.False(orchestrator.LastSubmission.Skip);
        Assert.Single(orchestrator.LastSubmission.Answers);
    }

    [Fact]
    public async Task SubmitResponse_SkipPayload_DelegatesToOrchestrator_AndReturnsOk()
    {
        var expectedResponseId = Guid.NewGuid();
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(expectedResponseId, QuestionnaireParticipationStatus.Skipped));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """
        {
            "version": "v1",
            "skip": true
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var responseDto = Assert.IsType<SocialQuestionnaireSubmissionResponseDto>(okResult.Value);

        Assert.Equal(expectedResponseId, responseDto.ResponseId);
        Assert.Equal("Skipped", responseDto.Status);

        Assert.NotNull(orchestrator.LastSubmission);
        Assert.True(orchestrator.LastSubmission.Skip);
    }

    [Fact]
    public async Task SubmitResponse_NonObjectJson_ReturnsBadRequest()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        using var doc = JsonDocument.Parse("[1, 2, 3]");

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Root payload must be a JSON object.", problem.Detail);
    }

    [Theory]
    [InlineData("""{"version": "v1", "__proto__": {"polluted": true}}""")]
    [InlineData("""{"version": "v1", "profileId": "00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"version": "v1", "userId": "user-123"}""")]
    [InlineData("""{"version": "v1", "extra": "not-allowed"}""")]
    [InlineData("""{}""")]
    public async Task SubmitResponse_MalformedOrDisallowedProperties_ReturnsBadRequest(string json)
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.NotNull(problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenOrchestratorRejectsSubmission_ReturnsBadRequest()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                ["Incomplete answers: question 'q02' is required."]));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """
        {
            "version": "v1",
            "answers": [
                { "questionId": "q01", "optionId": "1" }
            ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("Incomplete answers", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenProfileAccessDenied_ReturnsForbidden()
    {
        var orchestrator = StubSubmissionOrchestrator.Throwing(new ProfileAccessDeniedException());
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "skip": true}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task SubmitResponse_WhenProfileMissing_ReturnsNotFound()
    {
        var orchestrator = StubSubmissionOrchestrator.Throwing(new ProfileMissingException());
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "skip": true}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(notFoundResult.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public async Task SubmitResponse_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "skip": true}""";
        using var doc = JsonDocument.Parse(json);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.SubmitResponse(doc.RootElement, cts.Token));
    }

    #endregion

    #region Active Response Retrieval (GET /me) Verification

    [Fact]
    public async Task GetMyActiveResponse_WhenActiveResponseExists_ReturnsOkWithPrivacySafeProjection()
    {
        var responseId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var definition = QuestionnaireV1Definition.Create();
        var answers = new[]
        {
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, "q01", "1", DateTimeOffset.UtcNow),
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, "q02", "2", DateTimeOffset.UtcNow)
        };
        var activeResponseEntity = QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            definition,
            answers,
            skip: false,
            createdAtUtc: DateTimeOffset.UtcNow);

        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(responseId, QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService(activeResponseEntity);
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var result = await controller.GetMyActiveResponse(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var activeDto = Assert.IsType<SocialQuestionnaireActiveResponseDto>(okResult.Value);

        Assert.Equal(responseId, activeDto.ResponseId);
        Assert.Equal("v1", activeDto.Version);
        Assert.Equal(activeResponseEntity.Status.ToString(), activeDto.Status);
        Assert.Equal(2, activeDto.Answers.Count);
        Assert.Equal("q01", activeDto.Answers[0].QuestionId);
        Assert.Equal("1", activeDto.Answers[0].OptionId);

        // Security / Privacy: Ensure profileId is NOT exposed in the DTO
        var dtoProperties = typeof(SocialQuestionnaireActiveResponseDto)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();
        Assert.DoesNotContain("ProfileId", dtoProperties, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMyActiveResponse_WhenNoActiveResponse_ReturnsNotFound()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var result = await controller.GetMyActiveResponse(CancellationToken.None);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(notFoundResult.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public async Task GetMyActiveResponse_WhenProfileAccessDenied_ReturnsForbidden()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = StubLifecycleAccessService.Throwing(new ProfileAccessDeniedException());
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var result = await controller.GetMyActiveResponse(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetMyActiveResponse_WhenProfileMissing_ReturnsNotFound()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = StubLifecycleAccessService.Throwing(new ProfileMissingException());
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var result = await controller.GetMyActiveResponse(CancellationToken.None);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(notFoundResult.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Fact]
    public async Task GetMyActiveResponse_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.GetMyActiveResponse(cts.Token));
    }

    #endregion

    #region Preservation of Existing Questionnaire Definition Endpoint

    [Fact]
    public async Task QuestionnaireDefinitionsEndpoint_RemainsFunctional()
    {
        var sampleDto = new Andril.Social.DTOs.QuestionnaireDefinitionDto("v1", [
            new("q01", "Prompt", true, [new("1", "Option")])
        ]);
        var mockAccessService = new StubDefinitionAccessService(sampleDto);
        var controller = new SocialQuestionnaireDefinitionsController(mockAccessService);

        var actionResult = await controller.GetActiveDefinition(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var returnedDto = Assert.IsType<Andril.Social.DTOs.QuestionnaireDefinitionDto>(okResult.Value);

        Assert.Equal("v1", returnedDto.Version);
    }

    #endregion

    #region Helpers & Test Doubles

    private static string ReadProgramSource()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Andril.API",
                "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("ANDRIL repository source root was not found.");
    }

    private sealed class StubSubmissionOrchestrator : ISocialQuestionnairePersistentSubmissionOrchestrator
    {
        private readonly SocialQuestionnairePersistentSubmissionResult? _result;
        private readonly Exception? _exception;

        public QuestionnaireSubmission? LastSubmission { get; private set; }

        public StubSubmissionOrchestrator(SocialQuestionnairePersistentSubmissionResult result)
        {
            _result = result;
        }

        private StubSubmissionOrchestrator(Exception exception)
        {
            _exception = exception;
        }

        public static StubSubmissionOrchestrator Throwing(Exception exception) => new(exception);

        public Task<SocialQuestionnairePersistentSubmissionResult> SubmitAndPersistAsync(
            QuestionnaireSubmission? submission,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSubmission = submission;

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_result!);
        }
    }

    private sealed class StubLifecycleAccessService : ISocialQuestionnaireLifecycleAccessService
    {
        private readonly QuestionnaireResponseEntity? _activeResponse;
        private readonly Exception? _exception;

        public StubLifecycleAccessService(QuestionnaireResponseEntity? activeResponse = null)
        {
            _activeResponse = activeResponse;
        }

        private StubLifecycleAccessService(Exception exception)
        {
            _exception = exception;
        }

        public static StubLifecycleAccessService Throwing(Exception exception) => new(exception);

        public Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_activeResponse);
        }

        public Task CreateInitialResponseAsync(
            QuestionnaireResponseEntity initialResponse,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReplaceResponseAsync(
            Guid existingResponseId,
            QuestionnaireResponseEntity newResponse,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task WithdrawResponseAsync(
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StubDefinitionAccessService(Andril.Social.DTOs.QuestionnaireDefinitionDto dto) : ISocialQuestionnaireDefinitionAccessService
    {
        public Task<Andril.Social.DTOs.QuestionnaireDefinitionDto> GetActiveDefinitionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(dto);
        }
    }

    #endregion
}
