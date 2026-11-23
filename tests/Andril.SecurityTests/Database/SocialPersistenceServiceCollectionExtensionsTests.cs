using System;
using System.Data;
using System.Linq;
using Andril.API.Extensions;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Interfaces;
using Andril.Social.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Database;

public sealed class SocialPersistenceServiceCollectionExtensionsTests
{
    private const string DummyConnectionString = "Host=127.0.0.1;Database=andril_di_test;Username=test_user;Password=test_password";

    [Fact]
    public void AddSocialPersistence_NullArguments_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            SocialPersistenceServiceCollectionExtensions.AddSocialPersistence(null!, _ => { }));

        Assert.Throws<ArgumentNullException>(() =>
            services.AddSocialPersistence(null!));
    }

    [Fact]
    public void AddSocialPersistence_DefaultMigrationsHistory_ThrowsInvalidOperationExceptionOnResolution()
    {
        var services = new ServiceCollection();
        services.AddSocialPersistence(builder =>
            builder.UseNpgsql(DummyConnectionString));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<SocialDbContext>());

        Assert.Contains("Social requires migration history andril_social.__EFMigrationsHistory_Social.", ex.Message);
    }

    [Theory]
    [InlineData("__EFMigrationsHistory", "andril_social")]
    [InlineData("__EFMigrationsHistory_Social", "public")]
    [InlineData("__EFMigrationsHistory_Social", "andril_identity")]
    public void AddSocialPersistence_IncorrectMigrationsHistoryConfiguration_ThrowsInvalidOperationException(
        string historyTable,
        string historySchema)
    {
        var services = new ServiceCollection();
        services.AddSocialPersistence(builder =>
            builder.UseNpgsql(DummyConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(historyTable, historySchema)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<SocialDbContext>());

        Assert.Contains("Social requires migration history andril_social.__EFMigrationsHistory_Social.", ex.Message);
    }

    [Fact]
    public void AddSocialPersistence_RegistersExpectedServices_WithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSocialPersistence(builder =>
            builder.UseNpgsql(DummyConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)));

        var dbDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SocialDbContext));
        Assert.NotNull(dbDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, dbDescriptor.Lifetime);

        var adapterDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnaireReadAdapter));
        Assert.NotNull(adapterDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, adapterDescriptor.Lifetime);

        var repoDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IQuestionnairePersistenceRepository));
        Assert.NotNull(repoDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, repoDescriptor.Lifetime);

        var lookupDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SocialQuestionnaireResponseScopedLookupService));
        Assert.NotNull(lookupDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, lookupDescriptor.Lifetime);
    }

    [Fact]
    public void AddSocialPersistence_ResolvesServices_WithinScope_WithoutOpeningConnection()
    {
        var services = new ServiceCollection();
        services.AddSocialPersistence(builder =>
            builder.UseNpgsql(DummyConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)));

        using var provider = services.BuildServiceProvider();

        using (var scope1 = provider.CreateScope())
        {
            var db1 = scope1.ServiceProvider.GetRequiredService<SocialDbContext>();
            var adapter1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnaireReadAdapter>();
            var repo1 = scope1.ServiceProvider.GetRequiredService<IQuestionnairePersistenceRepository>();
            var lookup1 = scope1.ServiceProvider.GetRequiredService<SocialQuestionnaireResponseScopedLookupService>();

            Assert.NotNull(db1);
            Assert.IsType<PostgresSocialQuestionnaireReadAdapter>(adapter1);
            Assert.IsType<SocialQuestionnairePersistenceRepository>(repo1);
            Assert.NotNull(lookup1);

            // Fail-closed invariant: Resolving services must never open network connections
            Assert.Equal(ConnectionState.Closed, db1.Database.GetDbConnection().State);

            using var scope2 = provider.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<SocialDbContext>();
            Assert.NotSame(db1, db2);
        }
    }

    [Fact]
    public void AddSocialPersistence_Respects_EnforceIdentityDiscriminatorFlag()
    {
        var services = new ServiceCollection();
        services.AddSocialPersistence(
            builder => builder.UseNpgsql(DummyConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)),
            enforceIdentityDiscriminator: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireReadAdapter>();
        var repo = scope.ServiceProvider.GetRequiredService<IQuestionnairePersistenceRepository>();

        Assert.IsType<PostgresSocialQuestionnaireReadAdapter>(adapter);
        Assert.IsType<SocialQuestionnairePersistenceRepository>(repo);
    }
}
