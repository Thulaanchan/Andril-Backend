using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Moderation;
using Xunit;

namespace Andril.UnitTests.Moderation;

public sealed class ModerationReportSubmissionServiceTests
{
    private static readonly Guid AccountId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static ModerationReport CreateReport(
        string module = "learning")
    {
        return new ModerationReport(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            "profile",
            module,
            new DateTimeOffset(
                2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_RejectsNullVerifier()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new ModerationReportSubmissionService(
                null!, new FakeRepository()));

        Assert.Equal("accessVerifier", error.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNullRepository()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new ModerationReportSubmissionService(
                new FakeVerifier(), null!));

        Assert.Equal("repository", error.ParamName);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task DeniedModule_DoesNotWrite(string module)
    {
        var verifier = new FakeVerifier();
        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        var result = await service.SubmitAsync(
            AccountId, CreateReport(module));

        Assert.False(result);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task EmptyAccountId_DoesNotCallDependencies()
    {
        var verifier = new FakeVerifier();
        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        var result = await service.SubmitAsync(
            Guid.Empty, CreateReport());

        Assert.False(result);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task NullReport_DoesNotCallDependencies()
    {
        var verifier = new FakeVerifier();
        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        var result = await service.SubmitAsync(
            AccountId, null);

        Assert.False(result);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task AuthorizedSubmission_WritesExactlyOnce(
        string module)
    {
        var verifier = new FakeVerifier
        {
            OnCheck = (_, _, _) => Task.FromResult(true)
        };

        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        var report = CreateReport(module);
        using var cancellation = new CancellationTokenSource();

        var result = await service.SubmitAsync(
            AccountId, report, cancellation.Token);

        Assert.True(result);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(AccountId, verifier.LastAccountId);
        Assert.Same(report, verifier.LastReport);
        Assert.Equal(cancellation.Token, verifier.LastToken);

        Assert.Equal(1, repository.AddCalls);
        Assert.Same(report, repository.LastReport);
        Assert.Equal(cancellation.Token, repository.LastToken);
        Assert.Equal(0, repository.FindCalls);
    }

    [Fact]
    public async Task PreCancelledRequest_CallsNeitherDependency()
    {
        var verifier = new FakeVerifier();
        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SubmitAsync(
                AccountId, CreateReport(), cancellation.Token));

        Assert.Equal(0, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task CancellationAfterAuthorization_PreventsWrite()
    {
        using var cancellation = new CancellationTokenSource();

        var verifier = new FakeVerifier
        {
            OnCheck = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            }
        };

        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SubmitAsync(
                AccountId, CreateReport(), cancellation.Token));

        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task VerifierFailure_PreventsRepositoryWrite()
    {
        var verifier = new FakeVerifier
        {
            OnCheck = (_, _, _) =>
                Task.FromException<bool>(
                    new InvalidOperationException(
                        "Authorization unavailable"))
        };

        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitAsync(
                AccountId, CreateReport()));

        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task RepositoryFailure_PropagatesException()
    {
        var verifier = new FakeVerifier
        {
            OnCheck = (_, _, _) => Task.FromResult(true)
        };

        var repository = new FakeRepository
        {
            OnAdd = (_, _) => Task.FromException(
                new InvalidOperationException(
                    "Persistence unavailable"))
        };

        var service = new ModerationReportSubmissionService(
            verifier, repository);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitAsync(
                AccountId, CreateReport()));

        Assert.Equal(1, verifier.Calls);
        Assert.Equal(1, repository.AddCalls);
    }

    [Fact]
    public async Task RepeatedDeniedRequests_NeverWrite()
    {
        var verifier = new FakeVerifier();
        var repository = new FakeRepository();
        var service = new ModerationReportSubmissionService(
            verifier, repository);

        var report = CreateReport();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await service.SubmitAsync(
                AccountId, report);

            Assert.False(result);
        }

        Assert.Equal(5, verifier.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    private sealed class FakeVerifier
        : IModerationReportAccessVerifier
    {
        public int Calls { get; private set; }
        public Guid LastAccountId { get; private set; }
        public ModerationReport? LastReport { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<Guid, ModerationReport,
            CancellationToken, Task<bool>>? OnCheck { get; set; }

        public Task<bool> CanSubmitAsync(
            Guid verifiedAccountId,
            ModerationReport report,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAccountId = verifiedAccountId;
            LastReport = report;
            LastToken = cancellationToken;

            return OnCheck?.Invoke(
                verifiedAccountId,
                report,
                cancellationToken) ?? Task.FromResult(false);
        }
    }

    private sealed class FakeRepository
        : IModerationReportRepository
    {
        public int AddCalls { get; private set; }
        public int FindCalls { get; private set; }
        public ModerationReport? LastReport { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<ModerationReport,
            CancellationToken, Task>? OnAdd { get; set; }

        public Task AddAsync(
            ModerationReport report,
            CancellationToken cancellationToken = default)
        {
            AddCalls++;
            LastReport = report;
            LastToken = cancellationToken;

            return OnAdd?.Invoke(
                report,
                cancellationToken) ?? Task.CompletedTask;
        }

        public Task<ModerationReport?> FindByIdAsync(
            Guid reportId,
            string moduleScope,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult<ModerationReport?>(null);
        }
    }
}
