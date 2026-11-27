using Andril.API.Extensions;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Shared.Authentication;
using Andril.Social.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialQuestionnaireRuntimeCompositionTests
{
    [Theory]
    [InlineData(null, "social_runtime")]
    [InlineData("Host=127.0.0.1;Database=unused", null)]
    [InlineData("invalid connection", "social_runtime")]
    public void MissingOrInvalidConfigFailsBeforeRegistration(string? connection, string? role)
    {
        var services = new ServiceCollection();
        var config = Config(connection, role);
        Assert.Throws<InvalidOperationException>(() => services.AddVerifiedSocialQuestionnairePersistence(config));
        Assert.Empty(services);
    }

    [LocalPostgresFact]
    public async Task LiveVerifiedCompositionUsesRealScopedRepositoryAndRestrictedRole()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var actor = await fixture.SeedAsync();
        using var provider = Compose(fixture.Database.ConnectionString, fixture.Role, actor);
        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var repository = first.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleRepository>();
        Assert.IsType<PostgresSocialQuestionnaireLifecycleRepository>(repository);
        Assert.Same(repository, first.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleRepository>());
        Assert.NotSame(repository, second.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleRepository>());
        Assert.True(Assert.IsType<PostgresSocialQuestionnaireReadAdapter>(first.ServiceProvider
            .GetRequiredService<ISocialQuestionnaireReadAdapter>()).EnforceIdentityDiscriminator);
    }

    [LocalPostgresFact]
    public async Task LiveStartupRejectsPrivilegedRoleAndUnappliedSocialMigrations()
    {
        await using var fixture = await SocialLifecycleDatabase.CreateAsync();
        var actor = await fixture.SeedAsync();
        var login = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString).Username;
        using var privileged = Compose(fixture.Database.ConnectionString, login, actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Assert.Single(privileged.GetServices<IHostedService>()).StartAsync(CancellationToken.None));

        await using var pending = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        using var missing = Compose(pending.ConnectionString, fixture.Role, actor);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Assert.Single(missing.GetServices<IHostedService>()).StartAsync(CancellationToken.None));
        Assert.Contains("migrations must be applied", error.Message);
    }

    private static IConfiguration Config(string? connection, string? role) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AndrilAccounts"] = connection,
            ["SocialQuestionnaire:RuntimeRole"] = role
        }).Build();

    private static ServiceProvider Compose(string connection, string? role, ICurrentUser actor)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql(connection));
        services.AddSingleton(actor);
        services.AddVerifiedSocialQuestionnairePersistence(Config(connection, role));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
