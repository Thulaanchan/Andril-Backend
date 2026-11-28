using System.Net;
using Andril.API.Authentication;
using Andril.API.Authorization.Groups;
using Andril.Groups.Interfaces;
using Andril.Groups.Services;
using Andril.Persistence.Repositories.Groups;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Xunit;
using SocialHttpFixture = Andril.IntegrationTests.Database.SocialQuestionnaireHttpPostgresTests;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsRuntimePostgresTests
{
    [LocalPostgresFact]
    public async Task SelectedModulesStartWithTheVerifiedRestrictedRoleAndScopedProductionServices()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        using var host = new SharedGroupsHttpHost(fixture, hobbies: true);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.IsType<PostgresGroupEntryRepository>(services.GetRequiredService<IGroupEntryRepository>());
        Assert.IsType<PostgresGroupReadRepository>(services.GetRequiredService<IGroupDiscoveryReadRepository>());
        Assert.IsType<GroupMembershipOperationService>(services.GetRequiredService<IGroupMembershipOperationService>());
        Assert.NotNull(services.GetRequiredService<GroupManagementService>());
    }

    [LocalPostgresFact]
    public async Task LearningCanStartIndependentlyAndHobbiesRejectsUnavailableEntitlementAuthority()
    {
        await using var canonical = await SharedGroupsDatabase.CreateAsync();
        using var learning = new SharedGroupsHttpHost(canonical, social: false);
        using var learningClient = learning.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await learningClient.GetAsync("/health")).StatusCode);

        await using var fixture = await SharedGroupsDatabase.CreateAsync(withEntitlementSource: false);
        using var missingLearning = new SharedGroupsHttpHost(fixture, social: false);
        Assert.Contains("canonical database authority", Assert.Throws<InvalidOperationException>(() => missingLearning.CreateClient()).Message);
        using var missingSocial = new SharedGroupsHttpHost(fixture, learning: false);
        Assert.Contains("canonical database authority", Assert.Throws<InvalidOperationException>(() => missingSocial.CreateClient()).Message);
        using var hobbies = new SharedGroupsHttpHost(fixture, hobbies: true, social: false, learning: false);
        var failure = Assert.Throws<InvalidOperationException>(() => hobbies.CreateClient());
        Assert.Contains("canonical database authority", failure.Message);

        await using var admin = fixture.Open();
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA andril_economy;
            CREATE TABLE andril_economy.premium_entitlements (account_id uuid PRIMARY KEY);
            """);
        using var malformed = new SharedGroupsHttpHost(fixture, hobbies: true);
        Assert.Contains("canonical database authority", Assert.Throws<InvalidOperationException>(() => malformed.CreateClient()).Message);
    }

    [LocalPostgresFact]
    public async Task StartupRejectsDirectPrivilegedTableAccessEvenWhenTheRoleHasFunctionGrants()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        await using var admin = fixture.Open();
        var grant = $"GRANT SELECT ON andril_groups.module_blocks TO {fixture.Role}";
        await admin.Database.ExecuteSqlRawAsync(grant);
        using var host = new SharedGroupsHttpHost(fixture);
        var failure = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("privileges are invalid", failure.Message);
    }
}

internal sealed class SharedGroupsHttpHost(SharedGroupsDatabase store, bool enabled = true, bool hobbies = false, bool social = true, bool learning = true)
    : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SharedGroups:EnablePersistence"] = enabled.ToString(),
            ["SharedGroups:EnableLearning"] = learning.ToString(),
            ["SharedGroups:EnableHobbies"] = hobbies.ToString(),
            ["SharedGroups:EnableSocial"] = social.ToString(),
            ["SharedGroups:RuntimeRole"] = store.Role,
            ["ConnectionStrings:AndrilAccounts"] = store.Database.ConnectionString
        };
        foreach (var (name, version) in new[] { ("Terms", "fixture-terms-v1"), ("Privacy", "fixture-privacy-v1"), ("CommunityGuidelines", "fixture-community-v1") })
        {
            settings[$"AccountPolicies:{name}:Version"] = version;
            settings[$"AccountPolicies:{name}:Language"] = "en";
            settings[$"AccountPolicies:{name}:Url"] = $"https://example.invalid/{name}";
        }

        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        const string scheme = "groups-fixture";
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = scheme;
                options.DefaultChallengeScheme = scheme;
                options.DefaultForbidScheme = scheme;
            }).AddScheme<AuthenticationSchemeOptions, SocialHttpFixture.FixtureAuthentication>(scheme, _ => { });
            services.RemoveAll<ISupabaseUserVerifier>();
            services.AddSingleton<ISupabaseUserVerifier, SocialHttpFixture.FixtureConfirmation>();
            services.AddSingleton<IStartupFilter, SocialHttpFixture.Loopback>();
        });
    }
}
