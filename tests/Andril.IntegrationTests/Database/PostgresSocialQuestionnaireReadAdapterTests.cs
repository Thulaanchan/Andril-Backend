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

public sealed class PostgresSocialQuestionnaireReadAdapterTests
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

    [Fact]
    public void Constructor_RejectsNullDbContext()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgresSocialQuestionnaireReadAdapter(null!));
    }

    [Fact]
    public void Constructor_SetsEnforceIdentityDiscriminatorFlag()
    {
        using var context = CreateOfflineContext();
        var adapterDefault = new PostgresSocialQuestionnaireReadAdapter(context);
        var adapterEnforced = new PostgresSocialQuestionnaireReadAdapter(context, enforceIdentityDiscriminator: true);

        Assert.False(adapterDefault.EnforceIdentityDiscriminator);
        Assert.True(adapterEnforced.EnforceIdentityDiscriminator);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmptyProfileId_ReturnsNullWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        var result = await adapter.GetByIdAsync(Guid.Empty, Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmptyResponseId_ReturnsNullWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        var result = await adapter.GetByIdAsync(Guid.NewGuid(), Guid.Empty);

        Assert.Null(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task GetByIdAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task GetLatestByProfileIdAsync_WithEmptyProfileId_ReturnsNullWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        var result = await adapter.GetLatestByProfileIdAsync(Guid.Empty);

        Assert.Null(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task GetLatestByProfileIdAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.GetLatestByProfileIdAsync(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task ListByProfileIdAsync_WithEmptyProfileId_ReturnsEmptyWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        var result = await adapter.ListByProfileIdAsync(Guid.Empty, 10);

        Assert.Empty(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task ListByProfileIdAsync_WithNonPositiveMaxItems_ThrowsArgumentOutOfRangeException(int maxItems)
    {
        using var context = CreateOfflineContext();
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => adapter.ListByProfileIdAsync(Guid.NewGuid(), maxItems));
    }

    [Fact]
    public async Task ListByProfileIdAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ListByProfileIdAsync(Guid.NewGuid(), 10, cts.Token));
    }

    [Fact]
    public async Task VerifySocialProfileScopeAsync_WithEmptyProfileId_ReturnsFalseWithoutAccessingDatabase()
    {
        var guard = new RejectConnections();
        using var context = CreateOfflineContext(guard);
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        var result = await adapter.VerifySocialProfileScopeAsync(Guid.Empty);

        Assert.False(result);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task VerifySocialProfileScopeAsync_HonorsCancellationToken()
    {
        using var context = CreateOfflineContext();
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.VerifySocialProfileScopeAsync(Guid.NewGuid(), cts.Token));
    }

    [LocalPostgresFact]
    public async Task LivePostgres_GetByIdAsync_EnforcesProfileScope_AndEagerLoadsAnswers()
    {
        var env = Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(env)) return;

        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(env)
            .Options;

        using var context = new SocialDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var authorizedProfileId = Guid.NewGuid();
        var otherProfileId = Guid.NewGuid();
        var responseId = Guid.NewGuid();

        var definition = QuestionnaireV1Definition.Create();
        var now = DateTimeOffset.UtcNow;
        var answers = definition.Questions.Select(q =>
            new QuestionnaireAnswerEntity(Guid.NewGuid(), responseId, q.Id, "3", now)).ToList();

        var response = QuestionnaireResponseEntity.Create(
            responseId,
            authorizedProfileId,
            definition,
            answers,
            skip: false,
            now);

        context.QuestionnaireResponses.Add(response);
        await context.SaveChangesAsync();

        var adapter = new PostgresSocialQuestionnaireReadAdapter(context);

        // 1. Authorized read returns entity with eager-loaded answers
        var found = await adapter.GetByIdAsync(authorizedProfileId, responseId);
        Assert.NotNull(found);
        Assert.Equal(responseId, found.Id);
        Assert.Equal(authorizedProfileId, found.ProfileId);
        Assert.Equal(12, found.Answers.Count);
        Assert.Contains(found.Answers, a => a.QuestionId == "q01" && a.OptionId == "3");

        // 2. Cross-profile read returns null (Tenant Isolation)
        var unauthorizedResult = await adapter.GetByIdAsync(otherProfileId, responseId);
        Assert.Null(unauthorizedResult);
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
