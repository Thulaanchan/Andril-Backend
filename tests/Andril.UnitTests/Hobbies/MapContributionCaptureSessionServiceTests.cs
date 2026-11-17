
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionCaptureSessionServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid AttractionId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid SessionId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset ServerTime =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    // TEST 01
    [Fact]
    public async Task Start_AuthorizedProfile_CreatesSession()
    {
        var fixture = new TestFixture();

        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Service.StartAsync(
            AttractionId,
            cancellation.Token);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.SessionId);
        Assert.Equal(
            ProfileId,
            result.OwnerHobbiesModuleProfileId);

        Assert.Equal(AttractionId, result.AttractionId);
        Assert.Equal(ServerTime, result.StartedAtUtc);
        Assert.Equal(
            TimeSpan.Zero,
            result.StartedAtUtc.Offset);

        Assert.Null(result.CompletedAtUtc);

        Assert.Equal(1, fixture.AccessReader.Calls);
        Assert.Equal(
            AttractionId,
            fixture.AccessReader.LastAttractionId);

        Assert.Equal(
            cancellation.Token,
            fixture.AccessReader.LastToken);

        Assert.Equal(1, fixture.Repository.AddCalls);
        Assert.Same(result, fixture.Repository.AddedSession);

        Assert.Equal(
            cancellation.Token,
            fixture.Repository.LastToken);

        Assert.Equal(0, fixture.Repository.CompleteCalls);
        Assert.Equal(0, fixture.Repository.FindCalls);
    }

    // TEST 02
    [Fact]
    public async Task Start_EmptyAttractionId_IsRejected()
    {
        var fixture = new TestFixture();

        var result = await fixture.Service.StartAsync(
            Guid.Empty);

        Assert.Null(result);
        Assert.Equal(0, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 03-04
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_InvalidAuthorizedProfile_IsRejected(
        bool returnEmptyId)
    {
        var fixture = new TestFixture();

        fixture.AccessReader.ProfileToReturn =
            returnEmptyId ? Guid.Empty : null;

        var result = await fixture.Service.StartAsync(
            AttractionId);

        Assert.Null(result);
        Assert.Equal(1, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 05
    [Fact]
    public async Task Start_RepositoryFailure_IsNotReportedAsSuccess()
    {
        var fixture = new TestFixture();

        var failure = new InvalidOperationException(
            "Simulated persistence failure.");

        fixture.Repository.Failure = failure;

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.StartAsync(AttractionId));

        Assert.Same(failure, exception);
        Assert.Equal(1, fixture.Repository.AddCalls);
        Assert.Null(fixture.Repository.AddedSession);
        Assert.Equal(0, fixture.Repository.CompleteCalls);
    }

    // TEST 06-07
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_ReturnsRepositoryDecision(
        bool repositoryResult)
    {
        var fixture = new TestFixture();

        fixture.Repository.CompletionResult =
            repositoryResult;

        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Service.CompleteAsync(
            SessionId,
            AttractionId,
            cancellation.Token);

        Assert.Equal(repositoryResult, result);

        Assert.Equal(1, fixture.AccessReader.Calls);
        Assert.Equal(
            AttractionId,
            fixture.AccessReader.LastAttractionId);

        Assert.Equal(
            cancellation.Token,
            fixture.AccessReader.LastToken);

        Assert.Equal(1, fixture.Repository.CompleteCalls);
        Assert.Equal(
            SessionId,
            fixture.Repository.LastSessionId);

        Assert.Equal(
            ProfileId,
            fixture.Repository.LastProfileId);

        Assert.Equal(
            AttractionId,
            fixture.Repository.LastAttractionId);

        Assert.Equal<DateTimeOffset?>(
            ServerTime,
            fixture.Repository.LastCompletionTime);

        Assert.Equal(
            TimeSpan.Zero,
            fixture.Repository.LastCompletionTime!.Value.Offset);

        Assert.Equal(
            cancellation.Token,
            fixture.Repository.LastToken);

        Assert.Equal(0, fixture.Repository.AddCalls);
        Assert.Equal(0, fixture.Repository.FindCalls);
    }

    // TEST 08-09
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_InvalidIdentifiers_AreRejected(
        bool emptySessionId)
    {
        var fixture = new TestFixture();

        var sessionId =
            emptySessionId ? Guid.Empty : SessionId;

        var attractionId =
            emptySessionId ? AttractionId : Guid.Empty;

        var result = await fixture.Service.CompleteAsync(
            sessionId,
            attractionId);

        Assert.False(result);
        Assert.Equal(0, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 10-11
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_InvalidAuthorizedProfile_IsRejected(
        bool returnEmptyId)
    {
        var fixture = new TestFixture();

        fixture.AccessReader.ProfileToReturn =
            returnEmptyId ? Guid.Empty : null;

        var result = await fixture.Service.CompleteAsync(
            SessionId,
            AttractionId);

        Assert.False(result);
        Assert.Equal(1, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 12
    [Fact]
    public async Task Complete_RepositoryFailure_IsNotReportedAsSuccess()
    {
        var fixture = new TestFixture();

        var failure = new InvalidOperationException(
            "Simulated completion failure.");

        fixture.Repository.Failure = failure;

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CompleteAsync(
                    SessionId,
                    AttractionId));

        Assert.Same(failure, exception);
        Assert.Equal(1, fixture.Repository.CompleteCalls);
        Assert.Equal(0, fixture.Repository.AddCalls);
    }

    // TEST 13-14
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorizationFailure_DoesNotAccessRepository(
        bool startOperation)
    {
        var fixture = new TestFixture();

        var failure = new InvalidOperationException(
            "Simulated authorization dependency failure.");

        fixture.AccessReader.Failure = failure;

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InvokeOperationAsync(
                    fixture,
                    startOperation,
                    CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Equal(1, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 15-16
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledRequest_DoesNotAccessDependencies(
        bool startOperation)
    {
        var fixture = new TestFixture();

        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeOperationAsync(
                fixture,
                startOperation,
                cancellation.Token));

        Assert.Equal(0, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    // TEST 17-18
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledAfterAuthorization_DoesNotPersist(
        bool startOperation)
    {
        var fixture = new TestFixture();

        using var cancellation = new CancellationTokenSource();

        fixture.AccessReader.AfterResolve =
            cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeOperationAsync(
                fixture,
                startOperation,
                cancellation.Token));

        Assert.Equal(1, fixture.AccessReader.Calls);

        AssertNoRepositoryCalls(fixture.Repository);
    }

    private static async Task InvokeOperationAsync(
        TestFixture fixture,
        bool startOperation,
        CancellationToken cancellationToken)
    {
        if (startOperation)
        {
            await fixture.Service.StartAsync(
                AttractionId,
                cancellationToken);
        }
        else
        {
            await fixture.Service.CompleteAsync(
                SessionId,
                AttractionId,
                cancellationToken);
        }
    }

    private static void AssertNoRepositoryCalls(
        FakeRepository repository)
    {
        Assert.Equal(0, repository.AddCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.CompleteCalls);
    }

    // Test setup.
    private sealed class TestFixture
    {
        public FakeRepository Repository { get; } = new();

        public FakeAccessReader AccessReader { get; } = new();

        public MapContributionCaptureSessionService Service
        {
            get;
        }

        public TestFixture()
        {
            Service = new MapContributionCaptureSessionService(
                Repository,
                AccessReader,
                new FixedTimeProvider(ServerTime));
        }
    }

    // Provides deterministic server timestamps.
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow.ToUniversalTime();
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    // Simulates trusted contributor authorization.
    private sealed class FakeAccessReader
        : IMapCaptureSessionAccessReader
    {
        public Guid? ProfileToReturn { get; set; } = ProfileId;

        public int Calls { get; private set; }

        public Guid LastAttractionId { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Exception? Failure { get; set; }

        public Action? AfterResolve { get; set; }

        public Task<Guid?> ResolveAuthorizedProfileIdAsync(
            Guid attractionId,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            LastAttractionId = attractionId;
            LastToken = cancellationToken;

            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is Exception failure)
            {
                return Task.FromException<Guid?>(failure);
            }

            AfterResolve?.Invoke();

            return Task.FromResult(ProfileToReturn);
        }
    }

    // Simulates repository operations without a database.
    private sealed class FakeRepository
        : IMapContributionCaptureSessionRepository
    {
        public int AddCalls { get; private set; }

        public int FindCalls { get; private set; }

        public int CompleteCalls { get; private set; }

        public MapContributionCaptureSession? AddedSession
        {
            get;
            private set;
        }

        public Guid LastSessionId { get; private set; }

        public Guid LastProfileId { get; private set; }

        public Guid LastAttractionId { get; private set; }

        public DateTimeOffset? LastCompletionTime
        {
            get;
            private set;
        }

        public CancellationToken LastToken { get; private set; }

        public bool CompletionResult { get; set; } = true;

        public Exception? Failure { get; set; }

        public Task AddAsync(
            MapContributionCaptureSession session,
            CancellationToken cancellationToken = default)
        {
            AddCalls++;
            LastToken = cancellationToken;

            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is Exception failure)
            {
                return Task.FromException(failure);
            }

            AddedSession = session;

            return Task.CompletedTask;
        }

        public Task<MapContributionCaptureSession?> FindOwnedAsync(
            Guid sessionId,
            Guid ownerHobbiesModuleProfileId,
            Guid attractionId,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;

            throw new InvalidOperationException(
                "Unexpected repository lookup.");
        }

        public Task<bool> TryCompleteAsync(
            Guid sessionId,
            Guid ownerHobbiesModuleProfileId,
            Guid attractionId,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;

            LastSessionId = sessionId;
            LastProfileId = ownerHobbiesModuleProfileId;
            LastAttractionId = attractionId;
            LastCompletionTime = completedAt;
            LastToken = cancellationToken;

            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is Exception failure)
            {
                return Task.FromException<bool>(failure);
            }

            return Task.FromResult(CompletionResult);
        }
    }
}
