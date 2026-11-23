using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Andril.UnitTests.Social.Fakes;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class InMemoryQuestionnairePersistenceRepositoryTests
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset SyntheticTimestamp = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);

    private static QuestionnaireResponseEntity CreateResponse(Guid profileId, Guid? responseId = null, string version = "v1")
    {
        var respId = responseId ?? Guid.NewGuid();
        var def = QuestionnaireV1Definition.Create();
        var answers = new List<QuestionnaireAnswerEntity>
        {
            new(Guid.NewGuid(), respId, "q01", "1", SyntheticTimestamp)
        };

        return QuestionnaireResponseEntity.Create(
            respId,
            profileId,
            def,
            answers,
            skip: false,
            SyntheticTimestamp);
    }

    [Fact]
    public async Task SaveAsync_And_GetByIdAsync_RoundTripsSuccessfully()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response = CreateResponse(ProfileA);

        await repo.SaveAsync(ProfileA, response);
        var retrieved = await repo.GetByIdAsync(ProfileA, response.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(response.Id, retrieved.Id);
        Assert.Equal(ProfileA, retrieved.ProfileId);
        Assert.Equal(1, repo.StoredCount);
        Assert.True(repo.Contains(ProfileA, response.Id));
    }

    [Fact]
    public async Task GetByIdAsync_CrossProfileRetrieval_ReturnsNull()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response = CreateResponse(ProfileA);

        await repo.SaveAsync(ProfileA, response);
        var crossProfile = await repo.GetByIdAsync(ProfileB, response.Id);

        Assert.Null(crossProfile);
    }

    [Fact]
    public async Task SaveAsync_MismatchedProfile_ThrowsArgumentException()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response = CreateResponse(ProfileA);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(ProfileB, response));
        Assert.Equal("response", ex.ParamName);
    }

    [Fact]
    public async Task SaveAsync_EmptyProfileId_ThrowsArgumentException()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response = CreateResponse(ProfileA);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(Guid.Empty, response));
        Assert.Equal("profileId", ex.ParamName);
    }

    [Fact]
    public async Task SaveAsync_NullResponse_ThrowsArgumentNullException()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        await Assert.ThrowsAsync<ArgumentNullException>(() => repo.SaveAsync(ProfileA, null!));
    }

    [Fact]
    public async Task SaveAsync_UnapprovedVersion_ThrowsArgumentException()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository(new[] { "v2", "v3" });
        var response = CreateResponse(ProfileA, version: "v1");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveAsync(ProfileA, response));
        Assert.Equal("response", ex.ParamName);
        Assert.Contains("not an approved version", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_ApprovedVersion_Succeeds()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository(new[] { "v1" });
        var response = CreateResponse(ProfileA, version: "v1");

        await repo.SaveAsync(ProfileA, response);
        var retrieved = await repo.GetByIdAsync(ProfileA, response.Id);

        Assert.NotNull(retrieved);
    }

    [Fact]
    public async Task Clear_RemovesAllStoredResponses()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response1 = CreateResponse(ProfileA);
        var response2 = CreateResponse(ProfileB);

        await repo.SaveAsync(ProfileA, response1);
        await repo.SaveAsync(ProfileB, response2);
        Assert.Equal(2, repo.StoredCount);

        repo.Clear();

        Assert.Equal(0, repo.StoredCount);
        Assert.Null(await repo.GetByIdAsync(ProfileA, response1.Id));
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceledException()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        var response = CreateResponse(ProfileA);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetByIdAsync(ProfileA, response.Id, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SaveAsync(ProfileA, response, cts.Token));
    }

    [Fact]
    public async Task ConcurrentAccess_IsThreadSafe()
    {
        var repo = new InMemoryQuestionnairePersistenceRepository();
        const int profileCount = 20;
        var profiles = Enumerable.Range(0, profileCount).Select(_ => Guid.NewGuid()).ToList();

        var tasks = profiles.Select(async profileId =>
        {
            var response = CreateResponse(profileId);
            await repo.SaveAsync(profileId, response);
            var retrieved = await repo.GetByIdAsync(profileId, response.Id);
            Assert.NotNull(retrieved);
            Assert.Equal(profileId, retrieved.ProfileId);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(profileCount, repo.StoredCount);
    }
}
