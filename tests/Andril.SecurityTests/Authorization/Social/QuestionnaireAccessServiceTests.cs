using System.Text.Json;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Verifies the boundary security and module-level access control for QuestionnaireAccessService.
/// Tests ensure that trusted 'social' module access is required, fail-closed contracts are preserved,
/// and operations are aborted on cancellation or authorization rejection.
/// </summary>
public sealed class QuestionnaireAccessServiceTests
{
    private static QuestionnaireDefinition CreateDefinition(
        string version = "synth-v2",
        int questionCount = 2,
        string optionId = "yes") =>
        new(version, Enumerable.Range(1, questionCount)
            .Select(number => new QuestionDefinition(
                $"q{number}",
                $"Synthetic prompt {number}",
                true,
                new[] { new QuestionOption(optionId, "Synthetic label") }))
            .ToArray());

    private static QuestionnaireSubmission CreateSubmission(
        string version = "synth-v2",
        int answerCount = 2,
        string optionId = "yes") =>
        new(version, Enumerable.Range(1, answerCount)
            .Select(number => new QuestionnaireAnswer($"q{number}", optionId))
            .ToArray());

    private static QuestionnaireApplicationService CreateAppService(
        params QuestionnaireDefinition[] definitions) =>
        new(new QuestionnaireDefinitionCatalog(definitions));

    #region A. Constructor Security

    [Fact]
    public void Constructor_NullModuleAccess_ThrowsArgumentNullException()
    {
        var appService = CreateAppService(CreateDefinition());

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new QuestionnaireAccessService(null!, appService));

        Assert.Equal("moduleAccess", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullQuestionnaireService_ThrowsArgumentNullException()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new QuestionnaireAccessService(fakeAccess, null!));

        Assert.Equal("questionnaire", exception.ParamName);
    }

    #endregion

    #region B. Authorized Definition Retrieval

    [Fact]
    public async Task GetDefinitionAsync_Authorized_RequestsExactSocialModuleBeforeRetrieval()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var definition = await service.GetDefinitionAsync("synth-v2");

        Assert.NotNull(definition);
        var requestedModule = Assert.Single(fakeAccess.RequestedModules);
        Assert.Equal("social", requestedModule);
    }

    [Fact]
    public async Task GetDefinitionAsync_Authorized_ReturnsExpectedDefinitionForVersion()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(
            CreateDefinition("synth-v1", questionCount: 1, optionId: "opt-1"),
            CreateDefinition("synth-v2", questionCount: 2, optionId: "opt-2"));
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var definition = await service.GetDefinitionAsync("synth-v2");

        Assert.NotNull(definition);
        Assert.Equal("synth-v2", definition.Version);
        Assert.Equal(2, definition.Questions.Count);
        Assert.Equal("q1", definition.Questions[0].Id);
        Assert.Equal("opt-2", definition.Questions[0].Options[0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown-version")]
    [InlineData("SYNTH-V2")]
    public async Task GetDefinitionAsync_UnknownVersion_ReturnsNullWithoutFallback(string? version)
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition("synth-v2"));
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var result = await service.GetDefinitionAsync(version);

        Assert.Null(result);
        var requestedModule = Assert.Single(fakeAccess.RequestedModules);
        Assert.Equal("social", requestedModule);
    }

    #endregion

    #region C. Authorized Submission Validation

    [Fact]
    public async Task ValidateAsync_Authorized_RequestsExactSocialModuleBeforeValidation()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var submission = CreateSubmission();

        var result = await service.ValidateAsync(submission);

        Assert.NotNull(result);
        var requestedModule = Assert.Single(fakeAccess.RequestedModules);
        Assert.Equal("social", requestedModule);
    }

    [Fact]
    public async Task ValidateAsync_AuthorizedValidSubmission_ReturnsExpectedValidationResult()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var submission = CreateSubmission();

        var result = await service.ValidateAsync(submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_AuthorizedInvalidSubmission_PreservesValidationErrors()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var invalidSubmission = new QuestionnaireSubmission("synth-v2", new[]
        {
            new QuestionnaireAnswer("q1", "nonexistent-option")
        });

        var result = await service.ValidateAsync(invalidSubmission);

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Contains("INVALID_OPTION", result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_AuthorizedNullSubmission_PreservesInvalidSubmissionError()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var result = await service.ValidateAsync(null);

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Contains("INVALID_SUBMISSION", result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_AuthorizedSubmission_DoesNotMutateSubmissionData()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var answers = new List<QuestionnaireAnswer>
        {
            new("q1", "yes"),
            new("q2", "yes")
        };
        var submission = new QuestionnaireSubmission("synth-v2", answers);
        var jsonBefore = JsonSerializer.Serialize(submission);

        var result = await service.ValidateAsync(submission);

        Assert.True(result.IsValid);
        var jsonAfter = JsonSerializer.Serialize(submission);
        Assert.Equal(jsonBefore, jsonAfter);
        Assert.Same(answers, submission.Answers);
    }

    #endregion

    #region D. Denied and Pending Authorization

    [Fact]
    public async Task GetDefinitionAsync_DeniedAuthorization_PropagatesExceptionWithoutConversion()
    {
        var fakeAccess = FakePrivateModuleAccess.Deny(new ProfileAccessDeniedException());
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetDefinitionAsync("synth-v2"));

        Assert.Equal(new[] { "social" }, fakeAccess.RequestedModules);
    }

    [Fact]
    public async Task ValidateAsync_DeniedAuthorization_PropagatesExceptionWithoutConversion()
    {
        var fakeAccess = FakePrivateModuleAccess.Deny(new ProfileAccessDeniedException());
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var submission = CreateSubmission();

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.ValidateAsync(submission));

        Assert.Equal(new[] { "social" }, fakeAccess.RequestedModules);
    }

    [Fact]
    public async Task GetDefinitionAsync_PendingSecurityContract_RemainsFailClosed()
    {
        var fakeAccess = FakePrivateModuleAccess.PendingContract();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await Assert.ThrowsAsync<ProfileContractPendingException>(() =>
            service.GetDefinitionAsync("synth-v2"));

        Assert.Equal(new[] { "social" }, fakeAccess.RequestedModules);
    }

    [Fact]
    public async Task ValidateAsync_PendingSecurityContract_RemainsFailClosed()
    {
        var fakeAccess = FakePrivateModuleAccess.PendingContract();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);
        var submission = CreateSubmission();

        await Assert.ThrowsAsync<ProfileContractPendingException>(() =>
            service.ValidateAsync(submission));

        Assert.Equal(new[] { "social" }, fakeAccess.RequestedModules);
    }

    [Fact]
    public async Task Service_NeverRequestsAuthorizationUnderLearningOrHobbies()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await service.GetDefinitionAsync("synth-v2");
        await service.ValidateAsync(CreateSubmission());

        Assert.Equal(2, fakeAccess.RequestedModules.Count);
        Assert.All(fakeAccess.RequestedModules, module =>
        {
            Assert.Equal("social", module);
            Assert.NotEqual("learning", module);
            Assert.NotEqual("hobbies", module);
            Assert.NotEqual("dating", module);
        });
    }

    #endregion

    #region E. Cancellation and Isolation

    [Fact]
    public async Task GetDefinitionAsync_ForwardsCancellationTokenToModuleAccess()
    {
        using var cts = new CancellationTokenSource();
        var expectedToken = cts.Token;
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await service.GetDefinitionAsync("synth-v2", expectedToken);

        var receivedToken = Assert.Single(fakeAccess.ReceivedTokens);
        Assert.Equal(expectedToken, receivedToken);
    }

    [Fact]
    public async Task ValidateAsync_ForwardsCancellationTokenToModuleAccess()
    {
        using var cts = new CancellationTokenSource();
        var expectedToken = cts.Token;
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await service.ValidateAsync(CreateSubmission(), expectedToken);

        var receivedToken = Assert.Single(fakeAccess.ReceivedTokens);
        Assert.Equal(expectedToken, receivedToken);
    }

    [Fact]
    public async Task GetDefinitionAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledToken = cts.Token;
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetDefinitionAsync("synth-v2", cancelledToken));
    }

    [Fact]
    public async Task ValidateAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledToken = cts.Token;
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateAsync(CreateSubmission(), cancelledToken));
    }

    [Fact]
    public async Task GetDefinitionAsync_ResponseContainsOnlyAnswerFreeDtoFields()
    {
        var fakeAccess = FakePrivateModuleAccess.Allow();
        var appService = CreateAppService(CreateDefinition(questionCount: 1));
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var result = await service.GetDefinitionAsync("synth-v2");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = doc.RootElement;
        var topLevelProperties = root.EnumerateObject().Select(prop => prop.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "Questions", "Version" }, topLevelProperties);

        var question = Assert.Single(root.GetProperty("Questions").EnumerateArray());
        var questionProperties = question.EnumerateObject().Select(prop => prop.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "Id", "IsRequired", "Options", "Prompt" }, questionProperties);

        var option = Assert.Single(question.GetProperty("Options").EnumerateArray());
        var optionProperties = option.EnumerateObject().Select(prop => prop.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "Id", "Label" }, optionProperties);
    }

    [Fact]
    public async Task ValidateAsync_AuthorizationFailure_NeverReturnsValidationFailureResult()
    {
        var fakeAccess = FakePrivateModuleAccess.Deny(new UnauthorizedAccessException("Forbidden"));
        var appService = CreateAppService(CreateDefinition());
        var service = new QuestionnaireAccessService(fakeAccess, appService);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ValidateAsync(CreateSubmission()));

        Assert.Equal("Forbidden", exception.Message);
    }

    #endregion

    #region Test Doubles

    private sealed class FakePrivateModuleAccess : IPrivateModuleAccess
    {
        private readonly Func<string, CancellationToken, Task>? _handler;
        private readonly List<string> _requestedModules = new();
        private readonly List<CancellationToken> _receivedTokens = new();

        private FakePrivateModuleAccess(Func<string, CancellationToken, Task>? handler = null)
        {
            _handler = handler;
        }

        public static FakePrivateModuleAccess Allow() =>
            new((_, _) => Task.CompletedTask);

        public static FakePrivateModuleAccess Deny(Exception exception) =>
            new((_, _) => Task.FromException(exception));

        public static FakePrivateModuleAccess PendingContract() =>
            new((_, _) => Task.FromException(new ProfileContractPendingException()));

        public IReadOnlyList<string> RequestedModules => _requestedModules;
        public IReadOnlyList<CancellationToken> ReceivedTokens => _receivedTokens;

        public async Task RequireAsync(string module, CancellationToken cancellationToken = default)
        {
            _requestedModules.Add(module);
            _receivedTokens.Add(cancellationToken);

            if (_handler != null)
            {
                await _handler(module, cancellationToken);
            }
        }
    }

    #endregion
}
