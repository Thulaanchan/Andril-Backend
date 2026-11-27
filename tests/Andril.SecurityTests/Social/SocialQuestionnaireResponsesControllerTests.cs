using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Contracts.Social;
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

    #region Diagnostic and Sensitive Data Concealment

    [Fact]
    public async Task SubmitResponse_WhenDiagnosticMessageContainsSecretMarkers_ConcealsSecretMarkersFromProblemDetails()
    {
        const string secretMarker = "SYNTHETIC_SECRET_TOKEN_4928194";
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Rejected(
                "ERR_INTERNAL",
                $"Internal diagnostic failed with secret: {secretMarker}"));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.DoesNotContain(secretMarker, problem.Detail);

        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain(secretMarker, serialized);
        Assert.Equal("Questionnaire submission failed validation.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenErrorsContainPrivateProfileGuid_ConcealsProfileGuidFromProblemDetails()
    {
        var privateProfileId = Guid.NewGuid();
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                [$"Internal validation failed for profile {privateProfileId}"]));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.DoesNotContain(privateProfileId.ToString(), problem.Detail);

        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain(privateProfileId.ToString(), serialized);
        Assert.Equal("Questionnaire submission failed validation.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenErrorsContainSqlOrExceptionDiagnostics_ConcealsSqlFromProblemDetails()
    {
        const string sqlPayload = "SELECT * FROM public.social_questionnaires WHERE id = 'secret'";
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                [$"NpgsqlException: syntax error in query: {sqlPayload}"]));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.DoesNotContain("SELECT", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", problem.Detail, StringComparison.OrdinalIgnoreCase);

        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain("SELECT", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenParserErrorContainsSyntaxException_ConcealsExceptionDetails()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var badParser = new SocialQuestionnaireSubmissionBodyParser();
        var controllerWithParser = new SocialQuestionnaireResponsesController(orchestrator, lifecycle, badParser);

        var parseResult = badParser.Parse("""{"version": "v1", "answers": [""");
        Assert.False(parseResult.IsSuccess);
        Assert.Contains("Malformed JSON syntax", parseResult.Error);

        using var malformedDoc = JsonDocument.Parse("""{"version": "v1", "extra": "invalid"}""");
        var result = await controllerWithParser.SubmitResponse(malformedDoc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.NotNull(problem.Detail);
        Assert.DoesNotContain("Exception", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenApprovedIncompleteAnswersFeedback_PreservesActionableDetail()
    {
        const string actionableMessage = "Incomplete answers: question 'q05' is required.";
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                [actionableMessage]));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(actionableMessage, problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenContradictoryOutcome_EmptyResponseId_ReturnsBadRequest()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.Empty, QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Questionnaire submission outcome was invalid or contradictory.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenContradictoryOutcome_NotStartedStatus_ReturnsBadRequest()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.NotStarted));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Questionnaire submission outcome was invalid or contradictory.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenContradictoryOutcome_UndefinedStatusValue_ReturnsBadRequest()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), (QuestionnaireParticipationStatus)999));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Questionnaire submission outcome was invalid or contradictory.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenUnknownRootPropertyPassed_DoesNotEchoUnknownPropertyName()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "customUnknownProperty": "some_value"}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Unknown property in submission payload is not permitted.", problem.Detail);
        Assert.DoesNotContain("customUnknownProperty", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customUnknownProperty", JsonSerializer.Serialize(problem), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenUnknownSensitiveRootPropertyPassed_ConcealsSensitivePropertyCompletely()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "secretSuperAdminField": "token_12345"}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Failed to parse questionnaire submission.", problem.Detail);
        Assert.DoesNotContain("secretSuperAdminField", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretSuperAdminField", JsonSerializer.Serialize(problem), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenUnknownAnswerPropertyPassed_DoesNotEchoUnknownPropertyName()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1", "confidentialTelemetryKey": "xyz"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Unknown property in answer object is not permitted.", problem.Detail);
        Assert.DoesNotContain("confidentialTelemetryKey", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidentialTelemetryKey", JsonSerializer.Serialize(problem), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenDisallowedRootPropertyPassed_DoesNotEchoPropertyName()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "__proto__": {}}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Disallowed property detected in submission payload.", problem.Detail);
        Assert.DoesNotContain("__proto__", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitResponse_WhenDuplicateRootPropertyPassed_DoesNotEchoPropertyName()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "version": "v2"}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Duplicate property detected in root object.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenDuplicateAnswerPropertyPassed_DoesNotEchoPropertyName()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "questionId": "q02", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Duplicate property detected in answer object.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenPayloadSizeExceedsMaximum_ReturnsGenericPayloadSizeMessage()
    {
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(Guid.NewGuid(), QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        // Exceed 32 KB limit with large padding
        var largePadding = new string('x', 33 * 1024);
        var json = $"{{\"version\": \"v1\", \"answers\": [{{\"questionId\": \"q01\", \"optionId\": \"{largePadding}\"}}]}}";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Payload size exceeds maximum allowable limit.", problem.Detail);
    }

    [Fact]
    public async Task SubmitResponse_WhenApprovedIncompleteAnswersFeedback_PreservesQuestionIdTwoActionableDetail()
    {
        const string actionableMessage = "Incomplete answers: question 'q02' is required.";
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                [actionableMessage]));
        var lifecycle = new StubLifecycleAccessService();
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var json = """{"version": "v1", "answers": [{"questionId": "q01", "optionId": "1"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = await controller.SubmitResponse(doc.RootElement, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(actionableMessage, problem.Detail);
    }

    [Fact]
    public async Task GetMyActiveResponse_WhenStoredStatusIsUndefined_ReturnsInternalServerErrorWithoutExposingInvalidState()
    {
        var profileId = Guid.NewGuid();
        var ctor = typeof(QuestionnaireResponseEntity)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 6);

        var invalidEntity = (QuestionnaireResponseEntity)ctor.Invoke([
            Guid.NewGuid(),
            profileId,
            "v1",
            (QuestionnaireParticipationStatus)999,
            DateTimeOffset.UtcNow,
            Array.Empty<QuestionnaireAnswerEntity>()
        ]);

        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(invalidEntity.Id, QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService(invalidEntity);
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var actionResult = await controller.GetMyActiveResponse(CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Stored questionnaire response contains an invalid participation status.", problem.Detail);
        Assert.DoesNotContain("999", JsonSerializer.Serialize(problem));
    }

    [Fact]
    public async Task GetMyActiveResponse_ConcealsInternalProfileIdFromSerializedDto()
    {
        var profileId = Guid.NewGuid();
        var entity = CreateTestResponseEntity(profileId);
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(entity.Id, QuestionnaireParticipationStatus.Completed));
        var lifecycle = new StubLifecycleAccessService(entity);
        var controller = new SocialQuestionnaireResponsesController(orchestrator, lifecycle);

        var actionResult = await controller.GetMyActiveResponse(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<SocialQuestionnaireActiveResponseDto>(okResult.Value);

        var serialized = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(profileId.ToString(), serialized);
        Assert.DoesNotContain("profileId", serialized, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    public static IEnumerable<object[]> ApprovedValidationFeedback()
    {
        foreach (var code in new[] { "INVALID_SUBMISSION", "VERSION_MISMATCH", "SKIP_WITH_ANSWERS",
                     "INVALID_ANSWER", "DUPLICATE_ANSWER", "UNKNOWN_QUESTION", "INVALID_OPTION",
                     "INCOMPLETE_ANSWERS", "NOT_FOUND" })
            yield return [code];

        foreach (var question in QuestionnaireV1Definition.Create().Questions)
            yield return [$"Incomplete answers: question '{question.Id}' is required."];
    }

    [Theory]
    [MemberData(nameof(ApprovedValidationFeedback))]
    public async Task ValidationFeedback_OnlyExactApprovedMessagesReachProblemDetails(string message)
    {
        var controller = new SocialQuestionnaireResponsesController(
            new StubSubmissionOrchestrator(SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress, [message])), new StubLifecycleAccessService());

        var result = await controller.SubmitResponse(
            new QuestionnaireSubmission("v1", []), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal(message, problem.Detail);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(problem));
        Assert.Equal(message, serialized.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("Incomplete answers: question 'q99' is required.")]
    [InlineData("Incomplete answers: question 'Q02' is required.")]
    [InlineData("Incomplete answers: question 'q2' is required.")]
    [InlineData("Incomplete answers: question 'q02' is required. amber-orchid-728")]
    [InlineData("Incomplete answers: question 'q05' is required.\namber-orchid-728")]
    [InlineData("Incomplete answers: question 'q05' is required.\r\namber-orchid-728")]
    [InlineData("Incomplete answers amber-orchid-728")]
    [InlineData("Incomplete answers: question 'q02' is required.\u2028amber-orchid-728")]
    [InlineData("INVALID_OPTION amber-orchid-728")]
    [InlineData("INVALID_OPTION\namber-orchid-728")]
    [InlineData("invalid_option")]
    [InlineData("amber-orchid-728")]
    public async Task ValidationFeedback_UnknownOrExtendedMessagesAreNeverSerialized(string diagnostic)
    {
        var controller = new SocialQuestionnaireResponsesController(
            new StubSubmissionOrchestrator(SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress, [diagnostic])), new StubLifecycleAccessService());
        using var payload = JsonDocument.Parse("""{"version":"v1","answers":[]}""");

        var result = await controller.SubmitResponse(payload.RootElement, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Equal("Questionnaire submission failed validation.", problem.Detail);
        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain("amber-orchid-728", serialized);
        Assert.DoesNotContain(diagnostic, problem.Detail);
    }

    [Fact]
    public async Task ValidationFeedback_MixedDiagnosticsRetainOnlyApprovedMessagesInOrder()
    {
        const string required = "Incomplete answers: question 'q05' is required.";
        var controller = new SocialQuestionnaireResponsesController(
            new StubSubmissionOrchestrator(SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                QuestionnaireParticipationStatus.InProgress,
                [required + " amber-orchid-728", "INVALID_OPTION", required, "amber-orchid-728"])),
            new StubLifecycleAccessService());

        var result = await controller.SubmitResponse(new QuestionnaireSubmission("v1", []), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Equal($"INVALID_OPTION; {required}", problem.Detail);
        Assert.DoesNotContain("amber-orchid-728", JsonSerializer.Serialize(problem));
    }

    [Theory]
    [InlineData(false, QuestionnaireParticipationStatus.Completed)]
    [InlineData(true, QuestionnaireParticipationStatus.Skipped)]
    public async Task ValidationAllowlisting_PreservesSuccessfulSubmissionAndCancellation(
        bool skip, QuestionnaireParticipationStatus status)
    {
        var responseId = Guid.NewGuid();
        var orchestrator = new StubSubmissionOrchestrator(
            SocialQuestionnairePersistentSubmissionResult.Success(responseId, status));
        var controller = new SocialQuestionnaireResponsesController(orchestrator, new StubLifecycleAccessService());
        var answers = skip ? Array.Empty<QuestionnaireAnswer>() : QuestionnaireV1Definition.Create().Questions
            .Select(question => new QuestionnaireAnswer(question.Id, "3")).ToArray();
        var submission = new QuestionnaireSubmission("v1", answers, Skip: skip);

        var result = await controller.SubmitResponse(submission, CancellationToken.None);

        var dto = Assert.IsType<SocialQuestionnaireSubmissionResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(responseId, dto.ResponseId);
        Assert.Equal(status.ToString(), dto.Status);
        Assert.Same(submission, orchestrator.LastSubmission);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SubmitResponse(submission, cancellation.Token));
        Assert.NotNull(typeof(SocialQuestionnaireResponsesController).GetCustomAttribute<NonControllerAttribute>());
        Assert.NotNull(typeof(SocialQuestionnaireResponsesController).GetMethod(nameof(controller.SubmitResponse),
            [typeof(QuestionnaireSubmission), typeof(CancellationToken)])!.GetCustomAttribute<NonActionAttribute>());
    }

    #region Helpers & Test Doubles

    private static QuestionnaireResponseEntity CreateTestResponseEntity(Guid profileId)
    {
        var responseId = Guid.NewGuid();
        var definition = QuestionnaireV1Definition.Create();
        var answers = new[]
        {
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, "q01", "1", DateTimeOffset.UtcNow)
        };
        return QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            definition,
            answers,
            skip: false,
            createdAtUtc: DateTimeOffset.UtcNow);
    }

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
