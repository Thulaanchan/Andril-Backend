using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Extensions;
using Andril.Common.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class ModerationReportAuthorizationServiceCollectionExtensionsTests
{
    private static readonly Guid AccountId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ReportId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid ReporterId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly Guid ResourceId =
        Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public void Registration_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddModerationReportAuthorization();

        Assert.Same(services, result);
    }

    [Fact]
    public void Registration_RejectsNullServiceCollection()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ModerationReportAuthorizationServiceCollectionExtensions
                .AddModerationReportAuthorization(null!));
    }

    [Fact]
    public void Registration_ResolvesDenyAllVerifierByDefault()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var verifier = scope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        Assert.IsType<DenyAllModerationReportAccessVerifier>(
            verifier);
    }

    [Fact]
    public void RepeatedRegistration_DoesNotDuplicateVerifier()
    {
        var services = new ServiceCollection();

        services.AddModerationReportAuthorization();
        services.AddModerationReportAuthorization();

        var count = services.Count(descriptor =>
            descriptor.ServiceType ==
                typeof(IModerationReportAccessVerifier));

        Assert.Equal(1, count);
    }

    [Fact]
    public void Registration_PreservesExistingVerifier()
    {
        var services = new ServiceCollection();
        var existing = new StubVerifier();

        services.AddSingleton<IModerationReportAccessVerifier>(
            existing);

        services.AddModerationReportAuthorization();

        using var provider = services.BuildServiceProvider();

        var resolved = provider
            .GetRequiredService<IModerationReportAccessVerifier>();

        Assert.Same(existing, resolved);
    }

    [Fact]
    public void Registration_UsesScopedLifetime()
    {
        using var provider = CreateProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        var firstAgain = firstScope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        var second = secondScope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task DefaultVerifier_DeniesAllModules(
        string module)
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var verifier = scope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        var result = await verifier.CanSubmitAsync(
            AccountId,
            CreateReport(module));

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultVerifier_DeniesEmptyAccountId()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var verifier = scope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        var result = await verifier.CanSubmitAsync(
            Guid.Empty,
            CreateReport());

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultVerifier_DeniesNullReport()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var verifier = scope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        var result = await verifier.CanSubmitAsync(
            AccountId,
            null!);

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultVerifier_RespectsCancellation()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        var verifier = scope.ServiceProvider
            .GetRequiredService<IModerationReportAccessVerifier>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await verifier.CanSubmitAsync(
                AccountId,
                CreateReport(),
                cancellation.Token));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();

        services.AddModerationReportAuthorization();

        return services.BuildServiceProvider();
    }

    private static ModerationReport CreateReport(
        string module = "learning")
    {
        return new ModerationReport(
            ReportId,
            ReporterId,
            ResourceId,
            "profile",
            module,
            new DateTimeOffset(
                2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    }

    private sealed class StubVerifier
        : IModerationReportAccessVerifier
    {
        public Task<bool> CanSubmitAsync(
            Guid verifiedAccountId,
            ModerationReport report,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }
}
