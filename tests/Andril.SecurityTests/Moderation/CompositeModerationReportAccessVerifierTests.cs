using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Moderation;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class CompositeModerationReportAccessVerifierTests
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
            new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_RejectsNullReporterReader()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new CompositeModerationReportAccessVerifier(
                null!, new FakeResourceReader()));

        Assert.Equal("reporterReader", error.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNullResourceReader()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new CompositeModerationReportAccessVerifier(
                new FakeReporterReader(), null!));

        Assert.Equal("resourceReader", error.ParamName);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task BothAuthorized_AllowsSubmission(string module)
    {
        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var resource = new FakeResourceReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        var report = CreateReport(module);
        using var cancellation = new CancellationTokenSource();

        var result = await verifier.CanSubmitAsync(
            AccountId, report, cancellation.Token);

        Assert.True(result);
        Assert.Equal(1, reporter.Calls);
        Assert.Equal(1, resource.Calls);

        Assert.Equal(AccountId, reporter.LastAccountId);
        Assert.Equal(AccountId, resource.LastAccountId);

        Assert.Same(report, reporter.LastReport);
        Assert.Same(report, resource.LastReport);

        Assert.Equal(cancellation.Token, reporter.LastToken);
        Assert.Equal(cancellation.Token, resource.LastToken);
    }

    [Fact]
    public async Task ReporterDenied_SkipsResourceCheck()
    {
        var reporter = new FakeReporterReader();
        var resource = new FakeResourceReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        var result = await verifier.CanSubmitAsync(
            AccountId, CreateReport());

        Assert.False(result);
        Assert.Equal(1, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task ResourceDenied_PreventsAuthorization()
    {
        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        var result = await verifier.CanSubmitAsync(
            AccountId, CreateReport());

        Assert.False(result);
        Assert.Equal(1, reporter.Calls);
        Assert.Equal(1, resource.Calls);
    }

    [Fact]
    public async Task EmptyAccountId_SkipsBothReaders()
    {
        var reporter = new FakeReporterReader();
        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        var result = await verifier.CanSubmitAsync(
            Guid.Empty, CreateReport());

        Assert.False(result);
        Assert.Equal(0, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task NullReport_SkipsBothReaders()
    {
        var reporter = new FakeReporterReader();
        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        var result = await verifier.CanSubmitAsync(
            AccountId, null!);

        Assert.False(result);
        Assert.Equal(0, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task PreCancelledRequest_CallsNeitherReader()
    {
        var reporter = new FakeReporterReader();
        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.CanSubmitAsync(
                AccountId, CreateReport(), cancellation.Token));

        Assert.Equal(0, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task CancellationAfterReporter_PreventsResourceCheck()
    {
        using var cancellation = new CancellationTokenSource();

        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            }
        };

        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.CanSubmitAsync(
                AccountId, CreateReport(), cancellation.Token));

        Assert.Equal(1, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task CancellationAfterResource_PreventsAuthorization()
    {
        using var cancellation = new CancellationTokenSource();

        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var resource = new FakeResourceReader
        {
            Decision = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            }
        };

        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.CanSubmitAsync(
                AccountId, CreateReport(), cancellation.Token));

        Assert.Equal(1, reporter.Calls);
        Assert.Equal(1, resource.Calls);
    }

    [Fact]
    public async Task ReporterFailure_NeverCallsResource()
    {
        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) => Task.FromException<bool>(
                new InvalidOperationException("Reporter lookup failed"))
        };

        var resource = new FakeResourceReader();
        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.CanSubmitAsync(
                AccountId, CreateReport()));

        Assert.Equal(1, reporter.Calls);
        Assert.Equal(0, resource.Calls);
    }

    [Fact]
    public async Task ResourceFailure_NeverGrantsAuthorization()
    {
        var reporter = new FakeReporterReader
        {
            Decision = (_, _, _) => Task.FromResult(true)
        };

        var resource = new FakeResourceReader
        {
            Decision = (_, _, _) => Task.FromException<bool>(
                new InvalidOperationException("Resource lookup failed"))
        };

        var verifier = new CompositeModerationReportAccessVerifier(
            reporter, resource);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.CanSubmitAsync(
                AccountId, CreateReport()));

        Assert.Equal(1, reporter.Calls);
        Assert.Equal(1, resource.Calls);
    }

    [Fact]
    public async Task DenyAllReaders_NeverGrantAuthorization()
    {
        var verifier = new CompositeModerationReportAccessVerifier(
            new DenyAllModerationReportReporterProfileAccessReader(),
            new DenyAllModerationReportResourceAccessReader());

        Assert.False(await verifier.CanSubmitAsync(
            AccountId, CreateReport()));
    }

    private sealed class FakeReporterReader
        : IModerationReportReporterProfileAccessReader
    {
        public int Calls { get; private set; }
        public Guid LastAccountId { get; private set; }
        public ModerationReport? LastReport { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<Guid, ModerationReport, CancellationToken, Task<bool>>
            Decision { get; set; } =
                (_, _, _) => Task.FromResult(false);

        public Task<bool> IsAuthorizedReporterAsync(
            Guid verifiedAccountId,
            ModerationReport report,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAccountId = verifiedAccountId;
            LastReport = report;
            LastToken = cancellationToken;

            return Decision(
                verifiedAccountId, report, cancellationToken);
        }
    }

    private sealed class FakeResourceReader
        : IModerationReportResourceAccessReader
    {
        public int Calls { get; private set; }
        public Guid LastAccountId { get; private set; }
        public ModerationReport? LastReport { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<Guid, ModerationReport, CancellationToken, Task<bool>>
            Decision { get; set; } =
                (_, _, _) => Task.FromResult(false);

        public Task<bool> CanReportAsync(
            Guid verifiedAccountId,
            ModerationReport report,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAccountId = verifiedAccountId;
            LastReport = report;
            LastToken = cancellationToken;

            return Decision(
                verifiedAccountId, report, cancellationToken);
        }
    }
}
