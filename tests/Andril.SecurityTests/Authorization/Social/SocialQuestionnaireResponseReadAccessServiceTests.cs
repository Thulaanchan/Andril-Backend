using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireResponseReadAccessServiceTests
{
    [Fact]
    public async Task ReadScopedResponseAsync_WhenFoundWithinAuthorizedProfileScope_ReturnsFound()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);

        var definition = QuestionnaireV1Definition.Create();
        var responseEntity = QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            definition,
            [],
            skip: true,
            DateTimeOffset.UtcNow);

        var stubRepo = new StubQuestionnairePersistenceRepository(responseEntity);
        var scopedLookupService = new SocialQuestionnaireResponseScopedLookupService(stubRepo);
        var service = new SocialQuestionnaireResponseReadAccessService(stubProfileService, scopedLookupService);

        var result = await service.ReadScopedResponseAsync(responseId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.Found, result.Status);
    }

    [Fact]
    public async Task ReadScopedResponseAsync_WhenNotFound_ReturnsNotFound()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);

        var stubRepo = new StubQuestionnairePersistenceRepository(null);
        var scopedLookupService = new SocialQuestionnaireResponseScopedLookupService(stubRepo);
        var service = new SocialQuestionnaireResponseReadAccessService(stubProfileService, scopedLookupService);

        var result = await service.ReadScopedResponseAsync(responseId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ReadScopedResponseAsync_WhenWrongModule_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "hobbies", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubQuestionnairePersistenceRepository(null);
        var scopedLookupService = new SocialQuestionnaireResponseScopedLookupService(stubRepo);
        var service = new SocialQuestionnaireResponseReadAccessService(stubProfileService, scopedLookupService);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.ReadScopedResponseAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ReadScopedResponseAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubQuestionnairePersistenceRepository(null);
        var scopedLookupService = new SocialQuestionnaireResponseScopedLookupService(stubRepo);
        var service = new SocialQuestionnaireResponseReadAccessService(stubProfileService, scopedLookupService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReadScopedResponseAsync(Guid.NewGuid(), cts.Token));
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

    private sealed class StubQuestionnairePersistenceRepository(QuestionnaireResponseEntity? entity) : IQuestionnairePersistenceRepository
    {
        public Task<QuestionnaireResponseEntity?> GetByIdAsync(Guid profileId, Guid responseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(entity?.ProfileId == profileId && entity.Id == responseId ? entity : null);

        public Task SaveAsync(Guid profileId, QuestionnaireResponseEntity response, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
