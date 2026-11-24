using System;
using System.Data;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.SecurityTests.Database;

public sealed class SocialDbContextConfigurationSecurityTests
{
    [Fact]
    public void SocialDbContextFactory_CreatesContext_WithOfflineProtection()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());

        Assert.NotNull(context);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        // Attempting to open synchronous connection through EF Core must throw fail-closed InvalidOperationException
        var syncEx = Assert.Throws<InvalidOperationException>(() =>
        {
            context.Database.OpenConnection();
        });
        Assert.Contains("The Social design-time context only supports offline operations", syncEx.Message);

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SocialDbContextFactory_CreatesContext_WithAsyncOfflineProtection()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());

        Assert.NotNull(context);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        // Attempting to open async connection through EF Core must throw fail-closed InvalidOperationException
        var asyncEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.Database.OpenConnectionAsync();
        });
        Assert.Contains("The Social design-time context only supports offline operations", asyncEx.Message);

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SocialDbContextFactory_ConfiguresIsolatedMigrationsHistoryTable()
    {
        Assert.Equal("__EFMigrationsHistory_Social", SocialDbContextFactory.MigrationsHistoryTableName);
        Assert.Equal("andril_social", SocialDbContext.Schema);

        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var historyRepository = context.GetService<IHistoryRepository>();
        Assert.NotNull(historyRepository);

        var script = historyRepository.GetCreateScript();
        Assert.Contains(SocialDbContextFactory.MigrationsHistoryTableName, script);
        Assert.Contains(SocialDbContext.Schema, script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAValidConnectionString")]
    public void SocialDbContext_FailsClosed_OnInvalidConnectionString(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        using var context = new SocialDbContext(options);
        Assert.ThrowsAny<Exception>(() => context.Database.OpenConnection());
    }

    [Fact]
    public void SocialDbContext_DefaultSchema_IsStrictlyIsolatedToSocial()
    {
        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=andril_model_only;Username=unused")
            .Options;

        using var context = new SocialDbContext(options);
        var model = context.Model;

        Assert.Equal(SocialDbContext.Schema, model.GetDefaultSchema());
        Assert.Equal("andril_social", model.GetDefaultSchema());
    }
}
