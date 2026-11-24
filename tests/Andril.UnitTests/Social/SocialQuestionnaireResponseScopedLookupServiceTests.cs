using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialQuestionnaireResponseScopedLookupServiceTests
{
    private static readonly QuestionnaireDefinition ProductionV1Definition = QuestionnaireV1Definition.Create();

    private sealed class FakeQuestionnaireRepository : IQuestionnairePersistenceRepository
    {
        public Func<Guid, Guid, CancellationToken, Task<QuestionnaireResponseEntity?>>? OnGetById { get; set; }
        public Func<Guid, QuestionnaireResponseEntity, CancellationToken, Task>? OnSave { get; set; }
        public bool SaveCalled { get; private set; }

        public Task<QuestionnaireResponseEntity?> GetByIdAsync(Guid profileId, Guid responseId, CancellationToken cancellationToken = default) =>
            OnGetById is not null
                ? OnGetById(profileId, responseId, cancellationToken)
                : Task.FromResult<QuestionnaireResponseEntity?>(null);

        public Task SaveAsync(Guid profileId, QuestionnaireResponseEntity response, CancellationToken cancellationToken = default)
        {
            SaveCalled = true;
            return OnSave is not null
                ? OnSave(profileId, response, cancellationToken)
                : Task.CompletedTask;
        }
    }

    private static QuestionnaireResponseEntity CreateSampleResponse(Guid responseId, Guid profileId)
    {
        return QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task LookupResponseAsync_CorrectProfileAndResponse_ReturnsFound()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var sampleEntity = CreateSampleResponse(responseId, profileId);

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => Task.FromResult<QuestionnaireResponseEntity?>(sampleEntity)
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(profileId, responseId);

        Assert.True(result.IsFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.Found, result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal(responseId, result.Response.Id);
        Assert.Equal(profileId, result.Response.ProfileId);
        Assert.False(repo.SaveCalled);
    }

    [Fact]
    public async Task LookupResponseAsync_WrongProfileReturned_ReturnsProfileIdMismatch()
    {
        var requestedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var foreignEntity = CreateSampleResponse(responseId, foreignProfileId);

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => Task.FromResult<QuestionnaireResponseEntity?>(foreignEntity)
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(requestedProfileId, responseId);

        Assert.False(result.IsFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.ProfileIdMismatch, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task LookupResponseAsync_WrongResponseIdReturned_ReturnsResponseIdMismatch()
    {
        var profileId = Guid.NewGuid();
        var requestedResponseId = Guid.NewGuid();
        var foreignResponseId = Guid.NewGuid();
        var mismatchedEntity = CreateSampleResponse(foreignResponseId, profileId);

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => Task.FromResult<QuestionnaireResponseEntity?>(mismatchedEntity)
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(profileId, requestedResponseId);

        Assert.False(result.IsFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.ResponseIdMismatch, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task LookupResponseAsync_EmptyIdentifiers_ReturnsInvalidIdentity()
    {
        var repo = new FakeQuestionnaireRepository();
        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var emptyProfileResult = await service.LookupResponseAsync(Guid.Empty, Guid.NewGuid());
        var emptyResponseResult = await service.LookupResponseAsync(Guid.NewGuid(), Guid.Empty);
        var bothEmptyResult = await service.LookupResponseAsync(Guid.Empty, Guid.Empty);

        Assert.Equal(SocialQuestionnaireResponseLookupStatus.InvalidIdentity, emptyProfileResult.Status);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.InvalidIdentity, emptyResponseResult.Status);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.InvalidIdentity, bothEmptyResult.Status);
    }

    [Fact]
    public async Task LookupResponseAsync_MissingResponse_ReturnsNotFound()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => Task.FromResult<QuestionnaireResponseEntity?>(null)
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(profileId, responseId);

        Assert.True(result.IsNotFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.NotFound, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task LookupResponseAsync_RepositoryFailure_ReturnsDependencyFailure()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => throw new TimeoutException("Database connection timeout")
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(profileId, responseId);

        Assert.False(result.IsFound);
        Assert.False(result.IsNotFound);
        Assert.Equal(SocialQuestionnaireResponseLookupStatus.DependencyFailure, result.Status);
        Assert.Contains("TimeoutException", result.DiagnosticMessage);
    }

    [Fact]
    public async Task LookupResponseAsync_CancellationBeforeRead_ThrowsOperationCanceledException()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var repo = new FakeQuestionnaireRepository();
        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.LookupResponseAsync(profileId, responseId, cts.Token));
    }

    [Fact]
    public void LookupResult_Privacy_ConcealsConfidentialData()
    {
        var responseId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var entity = CreateSampleResponse(responseId, profileId);

        var foundResult = SocialQuestionnaireResponseLookupResult.Found(entity);

        var json = JsonSerializer.Serialize(foundResult);
        Assert.Equal("{}", json);

        var toString = foundResult.ToString();
        Assert.DoesNotContain(responseId.ToString(), toString);
        Assert.DoesNotContain(profileId.ToString(), toString);
        Assert.Contains("Found", toString);
    }

    [Fact]
    public async Task LookupResponseAsync_NoMutationOfReturnedData()
    {
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var sampleEntity = CreateSampleResponse(responseId, profileId);

        var repo = new FakeQuestionnaireRepository
        {
            OnGetById = (pid, rid, ct) => Task.FromResult<QuestionnaireResponseEntity?>(sampleEntity)
        };

        var service = new SocialQuestionnaireResponseScopedLookupService(repo);

        var result = await service.LookupResponseAsync(profileId, responseId);

        Assert.Same(sampleEntity, result.Response);
        Assert.False(repo.SaveCalled);
    }
}
