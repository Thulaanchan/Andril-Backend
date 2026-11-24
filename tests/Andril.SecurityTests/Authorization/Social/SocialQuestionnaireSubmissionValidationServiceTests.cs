using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireSubmissionValidationServiceTests
{
    private readonly QuestionnaireDefinitionCatalog _catalog = new([QuestionnaireV1Definition.Create()]);

    [Fact]
    public async Task ValidateSubmissionAsync_WithValidCompleteSubmission_ReturnsValid()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var service = new SocialQuestionnaireSubmissionValidationService(stubProfileService, appService);

        var validAnswers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswer($"q{i:D2}", "3"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", validAnswers);

        var result = await service.ValidateSubmissionAsync(submission, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateSubmissionAsync_WithValidSkip_ReturnsSkipped()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var service = new SocialQuestionnaireSubmissionValidationService(stubProfileService, appService);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        var result = await service.ValidateSubmissionAsync(submission, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Skipped, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateSubmissionAsync_WithInvalidVersion_ReturnsInvalid()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var service = new SocialQuestionnaireSubmissionValidationService(stubProfileService, appService);

        var submission = new QuestionnaireSubmission("v999", []);

        var result = await service.ValidateSubmissionAsync(submission, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("VERSION_MISMATCH", result.Errors);
    }

    [Fact]
    public async Task ValidateSubmissionAsync_WithWrongModule_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "learning", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var service = new SocialQuestionnaireSubmissionValidationService(stubProfileService, appService);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.ValidateSubmissionAsync(submission, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateSubmissionAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var appService = new QuestionnaireApplicationService(_catalog);
        var service = new SocialQuestionnaireSubmissionValidationService(stubProfileService, appService);

        var submission = new QuestionnaireSubmission("v1", [], Skip: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateSubmissionAsync(submission, cts.Token));
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
}
