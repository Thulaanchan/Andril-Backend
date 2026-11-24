using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialQuestionnairePersistenceRepositoryTests
{
    private const string OfflineConnectionString = "Host=localhost;Database=not_connected;Username=not_connected";

    private static SocialDbContext CreateOfflineContext(DbConnectionInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new SocialDbContext(builder.Options);
    }

    private static QuestionnaireResponseEntity CreateTestResponse(Guid profileId, Guid responseId, string optionValue = "3")
    {
        var definition = QuestionnaireV1Definition.Create();
        var now = DateTimeOffset.UtcNow;
        var answers = definition.Questions.Select(q =>
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, q.Id, optionValue, now)).ToList();

        return QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            definition,
            answers,
            skip: false,
            now);
    }

    [Fact]
    public void Constructor_RejectsNullDbContext()
    {
        Assert.Throws<ArgumentNullException>(() => new SocialQuestionnairePersistenceRepository(null!));
    }

    [Fact]
    public void Constructor_SetsEnforceIdentityDiscriminatorFlag()
    {
        using var context = CreateOfflineContext();
        var repoDefault = new SocialQuestionnairePersistenceRepository(context);
        var repoEnforced = new SocialQuestionnairePersistenceRepository(context, enforceIdentityDiscriminator: true);

        Assert.False(repoDefault.EnforceIdentityDiscriminator);
        Assert.True(repoEnforced.EnforceIdentityDiscriminator);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmptyProfileId_ReturnsNullWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var repo = new SocialQuestionnairePersistenceRepository(context);

        var result = await repo.GetByIdAsync(Guid.Empty, Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmptyResponseId_ReturnsNullWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var repo = new SocialQuestionnairePersistenceRepository(context);

        var result = await repo.GetByIdAsync(Guid.NewGuid(), Guid.Empty);

        Assert.Null(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task GetByIdAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var repo = new SocialQuestionnairePersistenceRepository(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task SaveAsync_WithEmptyProfileId_ThrowsArgumentException()
    {
        using var context = CreateOfflineContext();
        var repo = new SocialQuestionnairePersistenceRepository(context);
        var response = CreateTestResponse(Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(
            () => repo.SaveAsync(Guid.Empty, response));
    }

    [Fact]
    public async Task SaveAsync_WithNullResponse_ThrowsArgumentNullException()
    {
        using var context = CreateOfflineContext();
        var repo = new SocialQuestionnairePersistenceRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repo.SaveAsync(Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task SaveAsync_WithMismatchedProfileId_ThrowsInvalidOperationExceptionWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var repo = new SocialQuestionnairePersistenceRepository(context);

        var responseProfileId = Guid.NewGuid();
        var callerProfileId = Guid.NewGuid();
        var response = CreateTestResponse(responseProfileId, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(callerProfileId, response));

        Assert.Contains("Cross-profile persistence mismatch detected", ex.Message);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task SaveAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var repo = new SocialQuestionnairePersistenceRepository(context);
        var profileId = Guid.NewGuid();
        var response = CreateTestResponse(profileId, Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.SaveAsync(profileId, response, cts.Token));
    }

    [Fact]
    public async Task SaveAsync_WithEnforcedDiscriminator_RejectsInvalidProfile()
    {
        using var context = CreateOfflineContext();
        var repo = new SocialQuestionnairePersistenceRepository(context, enforceIdentityDiscriminator: true);
        var profileId = Guid.NewGuid();
        var response = CreateTestResponse(profileId, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(profileId, response));

        Assert.Contains("authorized 'social' module", ex.Message);
    }

    [LocalPostgresFact]
    public async Task LivePostgres_SaveAndGetByIdAsync_PersistsAtomically_AndEnforcesIsolation()
    {
        await using var database = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: true);
        await using var context = database.Open();

        var repo = new SocialQuestionnairePersistenceRepository(context);
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var otherProfileId = Guid.NewGuid();

        var response = CreateTestResponse(profileId, responseId);

        // 1. Atomic Save
        await repo.SaveAsync(profileId, response);

        // 2. Scoped Read
        var retrieved = await repo.GetByIdAsync(profileId, responseId);
        Assert.NotNull(retrieved);
        Assert.Equal(responseId, retrieved.Id);
        Assert.Equal(profileId, retrieved.ProfileId);
        Assert.Equal(12, retrieved.Answers.Count);

        // 3. Cross-profile Isolation (cannot read from foreign profile)
        var foreignRead = await repo.GetByIdAsync(otherProfileId, responseId);
        Assert.Null(foreignRead);

        // 4. Cross-profile Overwrite Violation
        var maliciousResponse = CreateTestResponse(otherProfileId, responseId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(otherProfileId, maliciousResponse));

        // 5. Existing-Response Overwrite Rejection (P-04 Safety Gate)
        var duplicateResponse = CreateTestResponse(profileId, responseId);
        var overwriteEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(profileId, duplicateResponse));
        Assert.Contains("Unapproved questionnaire response overwrite rejected", overwriteEx.Message);

        // 6. Existing Answers Preserved Without Mutation
        var preserved = await repo.GetByIdAsync(profileId, responseId);
        Assert.NotNull(preserved);
        Assert.Equal(responseId, preserved.Id);
        Assert.Equal(12, preserved.Answers.Count);
    }

    [LocalPostgresFact]
    public async Task LivePostgres_SaveAsync_ExistingResponseReplacement_FailsClosedAndPreservesAnswers()
    {
        await using var database = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: true);
        await using var context = database.Open();

        var repo = new SocialQuestionnairePersistenceRepository(context);
        var profileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        // 1. Persist original response with option value "3"
        var originalResponse = CreateTestResponse(profileId, responseId, optionValue: "3");
        var originalAnswerIds = originalResponse.Answers.Select(a => a.Id).ToHashSet();
        var originalCreatedAt = originalResponse.CreatedAtUtc;
        var originalStatus = originalResponse.Status;
        var originalVersion = originalResponse.Version;

        await repo.SaveAsync(profileId, originalResponse);

        // 2. Attempt replacement using different valid answer values ("5") and distinct answer GUIDs
        var replacementAttempt = CreateTestResponse(profileId, responseId, optionValue: "5");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(profileId, replacementAttempt));

        Assert.Contains("Unapproved questionnaire response overwrite rejected", ex.Message);
        Assert.Contains("Decision P-04", ex.Message);

        // 3. Attempt cross-profile overwrite on the existing response ID
        var otherProfileId = Guid.NewGuid();
        var crossProfileAttempt = CreateTestResponse(otherProfileId, responseId, optionValue: "5");
        var crossProfileEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.SaveAsync(otherProfileId, crossProfileAttempt));
        Assert.Contains("cannot overwrite a questionnaire response belonging to a different profile", crossProfileEx.Message);

        // 4. Open an independent DbContext to verify persistence state without change-tracker caching
        await using var independentContext = database.Open();
        var independentRepo = new SocialQuestionnairePersistenceRepository(independentContext);

        // 5. Reload original response via independent DbContext
        var reloaded = await independentRepo.GetByIdAsync(profileId, responseId);
        Assert.NotNull(reloaded);
        Assert.Equal(responseId, reloaded.Id);
        Assert.Equal(profileId, reloaded.ProfileId);
        Assert.Equal(originalCreatedAt, reloaded.CreatedAtUtc);
        Assert.Equal(originalStatus, reloaded.Status);
        Assert.Equal(originalVersion, reloaded.Version);

        // 6. Verify original answer IDs and values remain unchanged (option "3", not "5")
        Assert.Equal(12, reloaded.Answers.Count);
        foreach (var answer in reloaded.Answers)
        {
            Assert.Contains(answer.Id, originalAnswerIds);
            Assert.Equal("3", answer.OptionId);
        }

        // 7. Verify no additional answer rows were created in the database
        var totalAnswerRows = await independentContext.QuestionnaireAnswers.CountAsync();
        Assert.Equal(12, totalAnswerRows);
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }
    }
}
