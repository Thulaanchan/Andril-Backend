using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.DTOs;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Andril.Social.Services;
using Andril.UnitTests.Social.Fakes;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Integration and isolation QA tests covering the 12-question V1 questionnaire flow,
/// validation service, defensive copying, and repository storage integration.
/// </summary>
public sealed class QuestionnaireIntegrationQATests
{
    private static readonly Guid ProfileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset SyntheticTimestamp = new(2026, 11, 23, 16, 0, 0, TimeSpan.Zero);

    private static QuestionnaireDefinition ProductionV1 => QuestionnaireV1Definition.Create();

    private static QuestionnaireApplicationService CreateService() =>
        new(new QuestionnaireDefinitionCatalog(new[] { ProductionV1 }));

    #region A. V1 12-Question Flow & Validation

    [Fact]
    public void Validate_CompleteTwelveQuestionSubmission_SucceedsWithCompletedStatus()
    {
        var service = CreateService();
        var answers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswer($"q{i:D2}", "1"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", answers);

        var result = service.Validate(submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(11)]
    public void Validate_PartialAnswers_SucceedsWithInProgressStatus(int answerCount)
    {
        var service = CreateService();
        var answers = Enumerable.Range(1, answerCount)
            .Select(i => new QuestionnaireAnswer($"q{i:D2}", "1"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", answers);

        var result = service.Validate(submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.InProgress, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WrongQuestionnaireVersion_RejectsWithVersionMismatch()
    {
        var service = CreateService();
        var submission = new QuestionnaireSubmission("v2-unapproved", new[]
        {
            new QuestionnaireAnswer("q01", "1")
        });

        var result = service.Validate(submission);

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Contains("VERSION_MISMATCH", result.Errors);
    }

    [Fact]
    public void Validate_NullSubmission_RejectsWithInvalidSubmission()
    {
        var service = CreateService();

        var result = service.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Contains("INVALID_SUBMISSION", result.Errors);
    }

    [Fact]
    public void Validate_InvalidQuestionId_RejectsWithErrors()
    {
        var service = CreateService();
        var submission = new QuestionnaireSubmission("v1", new[]
        {
            new QuestionnaireAnswer("q99_invalid", "1")
        });

        var result = service.Validate(submission);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidOptionId_RejectsWithErrors()
    {
        var service = CreateService();
        var submission = new QuestionnaireSubmission("v1", new[]
        {
            new QuestionnaireAnswer("q01", "invalid_option_99")
        });

        var result = service.Validate(submission);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region B. Repository Storage and Profile Isolation

    [Fact]
    public async Task StorageIntegration_ValidSubmission_PersistsAndRetrievesUnderCorrectProfileScope()
    {
        var service = CreateService();
        var repo = new InMemoryQuestionnairePersistenceRepository(new[] { "v1" });

        var responseId = Guid.NewGuid();
        var answers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, $"q{i:D2}", "2", SyntheticTimestamp))
            .ToList();

        var responseEntity = QuestionnaireResponseEntity.Create(
            responseId,
            ProfileA,
            ProductionV1,
            answers,
            skip: false,
            SyntheticTimestamp);

        await repo.SaveAsync(ProfileA, responseEntity);

        var retrieved = await repo.GetByIdAsync(ProfileA, responseId);
        Assert.NotNull(retrieved);
        Assert.Equal(responseId, retrieved.Id);
        Assert.Equal(ProfileA, retrieved.ProfileId);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, retrieved.Status);
    }

    [Fact]
    public async Task StorageIntegration_CrossProfileIsolation_CannotAccessOtherProfileData()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository(new[] { "v1" });

        var responseA = QuestionnaireResponseEntity.Create(
            Guid.NewGuid(),
            ProfileA,
            ProductionV1,
            new List<QuestionnaireAnswerEntity>(),
            skip: true,
            SyntheticTimestamp);

        await repo.SaveAsync(ProfileA, responseA);

        var retrievedViaProfileB = await repo.GetByIdAsync(ProfileB, responseA.Id);
        Assert.Null(retrievedViaProfileB);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(ProfileB, responseA));
        Assert.Equal("response", ex.ParamName);
    }

    #endregion

    #region C. Privacy Safeguards & Defensive Copying

    [Fact]
    public void Entity_DefensiveCopying_IsPreservedAgainstExternalMutation()
    {
        var responseId = Guid.NewGuid();
        var mutableAnswers = new List<QuestionnaireAnswerEntity>
        {
            new(Guid.NewGuid(), responseId, "q01", "1", SyntheticTimestamp)
        };

        var responseEntity = QuestionnaireResponseEntity.Create(
            responseId,
            ProfileA,
            ProductionV1,
            mutableAnswers,
            skip: false,
            SyntheticTimestamp);

        Assert.Single(responseEntity.Answers);

        // Mutating external list must not affect the entity
        mutableAnswers.Add(new(Guid.NewGuid(), responseId, "q02", "2", SyntheticTimestamp));
        Assert.Single(responseEntity.Answers);
    }

    [Fact]
    public void Entity_SystemTextJson_ConcealsAllConfidentialAnswersAndProfileIds()
    {
        var responseId = Guid.NewGuid();
        var answers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, $"q{i:D2}", "1", SyntheticTimestamp))
            .ToList();

        var responseEntity = QuestionnaireResponseEntity.Create(
            responseId,
            ProfileA,
            ProductionV1,
            answers,
            skip: false,
            SyntheticTimestamp);

        var json = JsonSerializer.Serialize(responseEntity);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(ProfileA.ToString(), json);
        Assert.DoesNotContain(responseId.ToString(), json);
        Assert.DoesNotContain("q01", json);
    }

    [Fact]
    public void Entity_ToString_ReturnsPrivateConstantRepresentation()
    {
        var responseEntity = QuestionnaireResponseEntity.Create(
            Guid.NewGuid(),
            ProfileA,
            ProductionV1,
            new List<QuestionnaireAnswerEntity>(),
            skip: false,
            SyntheticTimestamp);

        var text = responseEntity.ToString();

        Assert.Equal("QuestionnaireResponseEntity [private]", text);
        Assert.DoesNotContain(ProfileA.ToString(), text);
    }

    #endregion

    #region D. Cancellation and Error Safety

    [Fact]
    public async Task Repository_CancelledToken_ThrowsOperationCanceledExceptionWithoutLeakingData()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository(new[] { "v1" });
        var responseEntity = QuestionnaireResponseEntity.Create(
            Guid.NewGuid(),
            ProfileA,
            ProductionV1,
            new List<QuestionnaireAnswerEntity>(),
            skip: false,
            SyntheticTimestamp);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SaveAsync(ProfileA, responseEntity, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetByIdAsync(ProfileA, responseEntity.Id, cts.Token));

        Assert.Equal(0, repo.StoredCount);
    }

    #endregion
}
