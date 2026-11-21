using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Moderation;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class DenyAllModerationReportReporterProfileAccessReaderTests
{
    private readonly DenyAllModerationReportReporterProfileAccessReader
        _reader = new();

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

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task EveryModule_IsDenied(string module)
    {
        var result = await _reader.IsAuthorizedReporterAsync(
            AccountId, CreateReport(module));

        Assert.False(result);
    }

    [Fact]
    public async Task EmptyAccountId_IsDenied()
    {
        var result = await _reader.IsAuthorizedReporterAsync(
            Guid.Empty, CreateReport());

        Assert.False(result);
    }

    [Fact]
    public async Task NullReport_IsDenied()
    {
        var result = await _reader.IsAuthorizedReporterAsync(
            AccountId, null!);

        Assert.False(result);
    }

    [Fact]
    public async Task DifferentAccounts_AreAllDenied()
    {
        for (var i = 0; i < 10; i++)
        {
            var result = await _reader.IsAuthorizedReporterAsync(
                Guid.NewGuid(), CreateReport());

            Assert.False(result);
        }
    }

    [Fact]
    public async Task RepeatedRequests_NeverGrantAccess()
    {
        var report = CreateReport();

        for (var i = 0; i < 100; i++)
        {
            var result = await _reader.IsAuthorizedReporterAsync(
                AccountId, report);

            Assert.False(result);
        }
    }

    [Fact]
    public async Task PreCancelledRequest_ThrowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reader.IsAuthorizedReporterAsync(
                AccountId,
                CreateReport(),
                cancellation.Token));
    }

    [Fact]
    public async Task NonCancelledToken_StillDenies()
    {
        using var cancellation = new CancellationTokenSource();

        var result = await _reader.IsAuthorizedReporterAsync(
            AccountId,
            CreateReport(),
            cancellation.Token);

        Assert.False(result);
    }

    [Fact]
    public void Reader_ImplementsExpectedContract()
    {
        Assert.IsAssignableFrom<
            IModerationReportReporterProfileAccessReader>(
                _reader);
    }
}
