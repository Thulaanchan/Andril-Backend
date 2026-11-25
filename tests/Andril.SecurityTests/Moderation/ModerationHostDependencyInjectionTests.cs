using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andril.Common.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class ModerationHostDependencyInjectionTests
{
    [Fact]
    public async Task ActualApiHost_ResolvesDenyAllVerifier()
    {
        using var factory = new ModerationHostFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        var verifiers = scope.ServiceProvider
            .GetServices<IModerationReportAccessVerifier>();

        var verifier = Assert.Single(verifiers);

        Assert.IsType<DenyAllModerationReportAccessVerifier>(
            verifier);

        var report = new ModerationReport(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            "profile",
            "learning",
            new DateTimeOffset(
                2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

        var result = await verifier.CanSubmitAsync(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            report);

        Assert.False(result);
    }

    private sealed class ModerationHostFactory
        : WebApplicationFactory<Program>
    {
        public ModerationHostFactory()
        {
            ClientOptions.BaseAddress =
                new Uri("https://localhost");
        }

        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["SupabaseAuth:ProjectUrl"] =
                            "https://andril-test.supabase.co",
                        ["SupabaseAuth:SigningAlgorithm"] =
                            "ES256",
                        ["SupabaseAuth:Audience"] =
                            "authenticated",
                        ["SupabaseIdentity:PublishableKey"] =
                            "sb_publishable_synthetic_test_fixture"
                    });
            });
        }
    }
}
