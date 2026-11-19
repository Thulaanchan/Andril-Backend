using Andril.Common.Moderation;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class DenyAllModerationReportAccessVerifierTests
{
    private static readonly Guid ValidAccountId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ValidReportId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid ValidReporterId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly Guid ValidResourceId =
        Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task AllModules_AreDenied(string module)
    {
        var verifier = new DenyAllModerationReportAccessVerifier();
        var report = CreateReport(module);

        var allowed = await verifier.CanSubmitAsync(
            ValidAccountId, report);

        Assert.False(allowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountIdentifiers_NeverGrantAccess(
        bool useEmptyAccountId)
    {
        var verifier = new DenyAllModerationReportAccessVerifier();
        var accountId = useEmptyAccountId
            ? Guid.Empty : ValidAccountId;

        var allowed = await verifier.CanSubmitAsync(
            accountId, CreateReport());

        Assert.False(allowed);
    }

    [Fact]
    public async Task NullReport_IsDenied()
    {
        var verifier = new DenyAllModerationReportAccessVerifier();

        var allowed = await verifier.CanSubmitAsync(
            ValidAccountId, null!);

        Assert.False(allowed);
    }

    [Fact]
    public async Task CancelledRequest_ThrowsOperationCanceledException()
    {
        var verifier = new DenyAllModerationReportAccessVerifier();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await verifier.CanSubmitAsync(
                ValidAccountId,
                CreateReport(),
                cancellation.Token));
    }

    [Fact]
    public async Task RepeatedRequests_AlwaysDeny()
    {
        var verifier = new DenyAllModerationReportAccessVerifier();
        var report = CreateReport();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var allowed = await verifier.CanSubmitAsync(
                ValidAccountId, report);

            Assert.False(allowed);
        }
    }

    [Fact]
    public void ImplementsAccessVerifierContract()
    {
        var verifier = new DenyAllModerationReportAccessVerifier();

        Assert.IsAssignableFrom<IModerationReportAccessVerifier>(
            verifier);
    }

    private static ModerationReport CreateReport(
        string module = "learning")
    {
        return new ModerationReport(
            ValidReportId,
            ValidReporterId,
            ValidResourceId,
            "profile",
            module,
            new DateTimeOffset(
                2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    }
}
