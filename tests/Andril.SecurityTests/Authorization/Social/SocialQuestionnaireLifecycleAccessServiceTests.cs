using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireLifecycleAccessServiceTests
{
    private static QuestionnaireResponseEntity CreateTestResponse(Guid profileId, Guid? responseId = null)
    {
        var definition = QuestionnaireV1Definition.Create();
        return QuestionnaireResponseEntity.Create(
            responseId ?? Guid.NewGuid(),
            profileId,
            definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenAuthorizedAndFound_ReturnsActiveEntity()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var expectedEntity = CreateTestResponse(profileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(expectedEntity);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        var result = await service.GetActiveResponseAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expectedEntity.Id, result!.Id);
        Assert.Equal(profileId, result.ProfileId);
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenAuthorizedAndNotFound_ReturnsNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        var result = await service.GetActiveResponseAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenWrongModule_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.NewGuid(), "learning", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveResponseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenRepositoryReturnsDifferentProfileResponse_ThrowsProfileAccessDeniedException()
    {
        var authorizedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(authorizedProfileId, "social", DateTimeOffset.UtcNow);
        var foreignEntity = CreateTestResponse(foreignProfileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(foreignEntity, bypassProfileFilter: true);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveResponseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenRepositoryReturnsContradictoryForeignEntity_FailsClosedAndDoesNotDiscloseData()
    {
        var authorizedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(authorizedProfileId, "social", DateTimeOffset.UtcNow);
        var contradictoryEntity = CreateTestResponse(foreignProfileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(contradictoryEntity, bypassProfileFilter: true);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        QuestionnaireResponseEntity? leaked = null;
        try
        {
            leaked = await service.GetActiveResponseAsync(CancellationToken.None);
        }
        catch (ProfileAccessDeniedException)
        {
            // Expected fail-closed behavior
        }

        Assert.Null(leaked);
    }

    [Fact]
    public async Task GetActiveResponseAsync_WhenEmptyProfileId_ThrowsProfileAccessDeniedException()
    {
        var profile = new PrivateProfileDto(Guid.Empty, "social", DateTimeOffset.UtcNow);
        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.GetActiveResponseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateInitialResponseAsync_WhenAuthorizedAndMatchingProfile_CallsRepository()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var initialEntity = CreateTestResponse(profileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await service.CreateInitialResponseAsync(initialEntity, CancellationToken.None);

        Assert.True(stubRepo.CreateInitialCalled);
        Assert.Equal(initialEntity.Id, stubRepo.LastInitialResponse?.Id);
    }

    [Fact]
    public async Task CreateInitialResponseAsync_WhenProfileMismatch_ThrowsProfileAccessDeniedException()
    {
        var authorizedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(authorizedProfileId, "social", DateTimeOffset.UtcNow);
        var foreignEntity = CreateTestResponse(foreignProfileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.CreateInitialResponseAsync(foreignEntity, CancellationToken.None));
        Assert.False(stubRepo.CreateInitialCalled);
    }

    [Fact]
    public async Task CreateInitialResponseAsync_WhenPayloadNull_ThrowsArgumentNullException()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.CreateInitialResponseAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceResponseAsync_WhenAuthorizedAndMatchingProfile_CallsRepository()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var existingId = Guid.NewGuid();
        var newEntity = CreateTestResponse(profileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await service.ReplaceResponseAsync(existingId, newEntity, CancellationToken.None);

        Assert.True(stubRepo.ReplaceCurrentCalled);
        Assert.Equal(existingId, stubRepo.LastExistingResponseId);
        Assert.Equal(newEntity.Id, stubRepo.LastNewResponse?.Id);
    }

    [Fact]
    public async Task ReplaceResponseAsync_WhenProfileMismatch_ThrowsProfileAccessDeniedException()
    {
        var authorizedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(authorizedProfileId, "social", DateTimeOffset.UtcNow);
        var foreignEntity = CreateTestResponse(foreignProfileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() =>
            service.ReplaceResponseAsync(Guid.NewGuid(), foreignEntity, CancellationToken.None));
        Assert.False(stubRepo.ReplaceCurrentCalled);
    }

    [Fact]
    public async Task ReplaceResponseAsync_WhenExistingIdEmpty_ThrowsArgumentException()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var newEntity = CreateTestResponse(profileId);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplaceResponseAsync(Guid.Empty, newEntity, CancellationToken.None));
    }

    [Fact]
    public async Task WithdrawResponseAsync_WhenAuthorized_CallsRepository()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var responseId = Guid.NewGuid();

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await service.WithdrawResponseAsync(responseId, CancellationToken.None);

        Assert.True(stubRepo.WithdrawCalled);
        Assert.Equal(responseId, stubRepo.LastWithdrawnResponseId);
    }

    [Fact]
    public async Task WithdrawResponseAsync_WhenIdEmpty_ThrowsArgumentException()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);

        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.WithdrawResponseAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Operations_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var responseEntity = CreateTestResponse(profileId);
        var stubProfileService = new StubPrivateProfileService(profile);
        var stubRepo = new StubLifecycleRepository(null);
        var service = new SocialQuestionnaireLifecycleAccessService(stubProfileService, stubRepo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetActiveResponseAsync(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateInitialResponseAsync(responseEntity, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReplaceResponseAsync(Guid.NewGuid(), responseEntity, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WithdrawResponseAsync(Guid.NewGuid(), cts.Token));
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

    private sealed class StubLifecycleRepository(
        QuestionnaireResponseEntity? activeResponse,
        bool bypassProfileFilter = false) : ISocialQuestionnaireLifecycleRepository
    {
        public bool CreateInitialCalled { get; private set; }
        public QuestionnaireResponseEntity? LastInitialResponse { get; private set; }

        public bool ReplaceCurrentCalled { get; private set; }
        public Guid? LastExistingResponseId { get; private set; }
        public QuestionnaireResponseEntity? LastNewResponse { get; private set; }

        public bool WithdrawCalled { get; private set; }
        public Guid? LastWithdrawnResponseId { get; private set; }

        public Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bypassProfileFilter)
            {
                return Task.FromResult(activeResponse);
            }
            return Task.FromResult(activeResponse?.ProfileId == profileId ? activeResponse : null);
        }

        public Task CreateInitialResponseAsync(Guid profileId, QuestionnaireResponseEntity initialResponse, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateInitialCalled = true;
            LastInitialResponse = initialResponse;
            return Task.CompletedTask;
        }

        public Task ReplaceCurrentResponseAsync(Guid profileId, Guid existingResponseId, QuestionnaireResponseEntity newResponse, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCurrentCalled = true;
            LastExistingResponseId = existingResponseId;
            LastNewResponse = newResponse;
            return Task.CompletedTask;
        }

        public Task WithdrawResponseAsync(Guid profileId, Guid responseId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WithdrawCalled = true;
            LastWithdrawnResponseId = responseId;
            return Task.CompletedTask;
        }
    }
}
