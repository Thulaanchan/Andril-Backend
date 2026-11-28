using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnairePersistentSubmissionOrchestratorTests
{
    private readonly QuestionnaireDefinitionCatalog _catalog = new([QuestionnaireV1Definition.Create()]);

    [Fact]
    public async Task SubmitAndPersistAsync_WithCompleteValidSubmission_PersistsAndReturnsSuccess()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var validAnswers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswer($"q{i:D2}", "3"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", validAnswers);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResponseId);
        Assert.NotEqual(Guid.Empty, result.ResponseId.Value);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);

        Assert.Equal(1, stubRepo.SaveCallCount);
        Assert.Equal(profileId, stubRepo.LastSavedProfileId);
        Assert.NotNull(stubRepo.LastSavedResponse);
        Assert.Equal(result.ResponseId.Value, stubRepo.LastSavedResponse.Id);
        Assert.Equal(profileId, stubRepo.LastSavedResponse.ProfileId);
        Assert.Equal(12, stubRepo.LastSavedResponse.Answers.Count);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithPartialValidSubmission_PersistsAndReturnsInProgress()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        // 3 valid answers out of 12 represents in-progress participation
        var partialAnswers = Enumerable.Range(1, 3)
            .Select(i => new QuestionnaireAnswer($"q{i:D2}", "3"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", partialAnswers);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResponseId);
        Assert.Equal(QuestionnaireParticipationStatus.InProgress, result.Status);
        Assert.Empty(result.Errors);
        Assert.Equal(1, stubRepo.SaveCallCount);
        Assert.Equal(3, stubRepo.LastSavedResponse!.Answers.Count);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithValidSkip_PersistsAndReturnsSkipped()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResponseId);
        Assert.Equal(QuestionnaireParticipationStatus.Skipped, result.Status);
        Assert.Empty(result.Errors);

        Assert.Equal(1, stubRepo.SaveCallCount);
        Assert.Equal(profileId, stubRepo.LastSavedProfileId);
        Assert.NotNull(stubRepo.LastSavedResponse);
        Assert.Empty(stubRepo.LastSavedResponse.Answers);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithInvalidOption_ReturnsValidationFailed_DoesNotPersist()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        // Non-existent option ID fails domain validation
        var invalidAnswers = new[]
        {
            new QuestionnaireAnswer("q01", "non_existent_option_value")
        };
        var submission = new QuestionnaireSubmission("v1", invalidAnswers);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ResponseId);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("INVALID_OPTION", result.Errors);
        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithSkipAndAnswers_ReturnsValidationFailed_DoesNotPersist()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        // Skip cannot contain answers
        var submission = new QuestionnaireSubmission("v1", [new QuestionnaireAnswer("q01", "3")], Skip: true);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ResponseId);
        Assert.Contains("SKIP_WITH_ANSWERS", result.Errors);
        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithUnrecognizedVersion_ReturnsRejected_DoesNotPersist()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v999_nonexistent", []);

        var result = await orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Contains("VERSION_MISMATCH", result.Errors);
        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithNullSubmission_ReturnsRejected_DoesNotPersist()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var result = await orchestrator.SubmitAndPersistAsync(null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Contains("INVALID_SUBMISSION", result.Errors);
        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithMismatchedModule_ThrowsProfileAccessDeniedException()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "hobbies", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None));

        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WithEmptyProfileId_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.Empty, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None));

        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.SubmitAndPersistAsync(submission, cts.Token));

        Assert.Equal(0, stubRepo.SaveCallCount);
    }

    [Fact]
    public async Task SubmitAndPersistAsync_WhenRepositoryThrows_PropagatesExceptionFailClosed()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository { ShouldThrow = true };
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            stubProfileService,
            _catalog,
            appService,
            stubRepo);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SubmitAndPersistAsync(submission, CancellationToken.None));
    }

    [Fact]
    public void Result_ToString_ConcealsConfidentialInformation()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        var result = SocialQuestionnairePersistentSubmissionResult.Success(responseId, QuestionnaireParticipationStatus.Completed);
        var str = result.ToString();

        Assert.DoesNotContain(profileId.ToString(), str);
        Assert.DoesNotContain(responseId.ToString(), str);
        Assert.Contains("Completed", str);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_OnNullParameters()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var stubRepo = new TrackingQuestionnaireLifecycleRepository();

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnairePersistentSubmissionOrchestrator(null!, _catalog, appService, stubRepo));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnairePersistentSubmissionOrchestrator(stubProfileService, null!, appService, stubRepo));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnairePersistentSubmissionOrchestrator(stubProfileService, _catalog, null!, stubRepo));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialQuestionnairePersistentSubmissionOrchestrator(stubProfileService, _catalog, appService, null!));
    }

    [Fact]
    public async Task DuplicateInitialResponseReturnsStableConflictWithoutAnId()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var repository = new TrackingQuestionnaireLifecycleRepository { HasCurrentResponse = true };
        var orchestrator = new SocialQuestionnairePersistentSubmissionOrchestrator(
            new StubPrivateProfileService(profile), _catalog, new QuestionnaireApplicationService(_catalog), repository);

        var result = await orchestrator.SubmitAndPersistAsync(new QuestionnaireSubmission("v1", [], Skip: true));

        Assert.False(result.IsSuccess);
        Assert.Null(result.ResponseId);
        Assert.Equal(SocialQuestionnairePersistentSubmissionResult.CurrentResponseExists, Assert.Single(result.Errors));
        Assert.Equal(0, repository.SaveCallCount);
    }
    private sealed class StubPrivateProfileService(PrivateProfileDto profile) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action(profile);
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => Task.FromResult(profile);
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => Task.FromResult(profile);
    }

    private sealed class TrackingQuestionnaireLifecycleRepository : ISocialQuestionnaireLifecycleRepository
    {
        public Guid? LastSavedProfileId { get; private set; }
        public QuestionnaireResponseEntity? LastSavedResponse { get; private set; }
        public int SaveCallCount { get; private set; }
        public bool ShouldThrow { get; set; }
        public bool HasCurrentResponse { get; init; }

        public Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<QuestionnaireResponseEntity?>(null);
        }

        public Task ReplaceCurrentResponseAsync(Guid profileId, Guid existingResponseId,
            QuestionnaireResponseEntity newResponse, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WithdrawResponseAsync(Guid profileId, Guid responseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateInitialResponseAsync(Guid profileId, QuestionnaireResponseEntity response, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasCurrentResponse) throw new SocialQuestionnaireCurrentResponseConflictException();
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            LastSavedProfileId = profileId;
            LastSavedResponse = response;
            SaveCallCount++;
            return Task.CompletedTask;
        }
    }
}
