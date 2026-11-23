using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireDefinitionAccessServiceTests
{
    [Fact]
    public async Task GetActiveDefinitionAsync_WithValidAuthorizedSocialProfile_ReturnsV1Definition()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        var result = await service.GetActiveDefinitionAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("v1", result.Version);
        Assert.Equal(12, result.Questions.Count);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("unknown")]
    public async Task GetActiveDefinitionAsync_WithWrongModule_ThrowsProfileAccessDeniedException(string wrongModule)
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), wrongModule, DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveDefinitionAsync_WithEmptyProfileId_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.Empty, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveDefinitionAsync_WhenAuthorizationFails_PreservesProfileAccessDeniedException()
    {
        var stubProfileService = new FaultyPrivateProfileService(new ProfileAccessDeniedException());
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveDefinitionAsync_WhenProfileMissing_PreservesProfileMissingException()
    {
        var stubProfileService = new FaultyPrivateProfileService(new ProfileMissingException());
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        await Assert.ThrowsAsync<ProfileMissingException>(() =>
            service.GetActiveDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveDefinitionAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profile = new PrivateProfileDto(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var catalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var appService = new QuestionnaireApplicationService(catalog);
        var service = new SocialQuestionnaireDefinitionAccessService(stubProfileService, appService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetActiveDefinitionAsync(cts.Token));
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

    private sealed class FaultyPrivateProfileService(Exception exception) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken) =>
            Task.FromException<T>(exception);

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => Task.FromException<PrivateProfileDto>(exception);
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => Task.FromException<PrivateProfileDto>(exception);
    }
}
