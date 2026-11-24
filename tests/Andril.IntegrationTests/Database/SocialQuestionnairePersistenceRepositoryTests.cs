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

    private static QuestionnaireResponseEntity CreateTestResponse(Guid profileId, Guid responseId)
    {
        var definition = QuestionnaireV1Definition.Create();
        var now = DateTimeOffset.UtcNow;
        var answers = definition.Questions.Select(q =>
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, q.Id, "3", now)).ToList();

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
