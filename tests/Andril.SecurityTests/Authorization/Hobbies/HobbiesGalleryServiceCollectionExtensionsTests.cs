using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Hobbies;
using Andril.API.Extensions;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Configurations;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Andril.SecurityTests.Authorization.Hobbies;

public sealed class HobbiesGalleryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHobbiesGallery_RejectsUnconfiguredOptions()
    {
        var services = new ServiceCollection().AddHobbiesGallery<DenyAllTestActorContext>(_ => { });
        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<HobbiesGalleryDbContext>());
        Assert.Equal("Gallery requires migration history andril_hobbies.__EFMigrationsHistory_Gallery.", error.Message);
    }

    [Fact]
    public void AddHobbiesGallery_PreservesCallerSuppliedConnectionInstance()
    {
        using var connection = new NpgsqlConnection("Host=127.0.0.1;Database=unused;Username=unused");
        var interceptor = new RejectConnectionOpen();
        var services = new ServiceCollection().AddHobbiesGallery<DenyAllTestActorContext>(options =>
            options.UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureGalleryHistory)
                .AddInterceptors(interceptor));
        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HobbiesGalleryDbContext>();

        Assert.Same(connection, context.Database.GetDbConnection());
        Assert.True(context.Database.IsNpgsql());
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(0, interceptor.Attempts);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("__EFMigrationsHistory_Map", "andril_hobbies")]
    [InlineData("__EFMigrationsHistory", "andril_identity")]
    [InlineData("__EFMigrationsHistory_Gallery", "public")]
    [InlineData("__EFMigrationsHistory_Gallery", null)]
    [InlineData("__EFMigrationsHistory", "andril_hobbies")]
    public void AddHobbiesGallery_RejectsMissingOrIncorrectHistoryWithoutConnecting(string? table, string? schema)
    {
        var interceptor = new RejectConnectionOpen();
        var services = new ServiceCollection().AddHobbiesGallery<DenyAllTestActorContext>(options =>
        {
            options.UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused", postgres =>
            {
                if (table is not null)
                    postgres.MigrationsHistoryTable(table, schema);
            });
            options.AddInterceptors(interceptor);
        });
        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<DbContextOptions<HobbiesGalleryDbContext>>());
        Assert.Equal("Gallery requires migration history andril_hobbies.__EFMigrationsHistory_Gallery.", error.Message);
        Assert.Equal(0, interceptor.Attempts);
    }

    [Fact]
    public async Task AddHobbiesGallery_PreservesEffectiveOptionsAndDoesNotRunMigrations()
    {
        const string connection = "Host=127.0.0.1;Port=55439;Database=andril_gallery_composition;Username=unused;Timeout=3";
        var interceptor = new RejectConnectionOpen();
        var configured = 0;
        var services = new ServiceCollection().AddHobbiesGallery<DenyAllTestActorContext>(options =>
        {
            configured++;
            options.UseNpgsql(connection, postgres =>
            {
                HobbiesDatabaseOptions.ConfigureGalleryHistory(postgres);
                postgres.EnableRetryOnFailure(4, TimeSpan.FromSeconds(2), null);
                postgres.CommandTimeout(7);
            }).AddInterceptors(interceptor);
        });
        Assert.Equal(0, configured);
        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HobbiesGalleryDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<HobbiesGalleryDbContext>>();
        var relational = Assert.Single(options.Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal("__EFMigrationsHistory_Gallery", relational.MigrationsHistoryTableName);
        Assert.Equal("andril_hobbies", relational.MigrationsHistoryTableSchema);
        Assert.Equal(connection, relational.ConnectionString);
        Assert.Equal(7, relational.CommandTimeout);
        Assert.True(context.Database.IsNpgsql());
        var retry = Assert.IsAssignableFrom<ExecutionStrategy>(context.Database.CreateExecutionStrategy());
        Assert.True(retry.RetriesOnFailure);
        Assert.Equal(4, retry.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.MaxRetryDelay);
        Assert.Contains(interceptor, options.FindExtension<CoreOptionsExtension>()!.Interceptors!);
        Assert.Equal(1, configured);
        Assert.Equal(0, interceptor.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        // Explicit opening confirms that the original interceptor is still active.
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync());
        Assert.Equal(1, interceptor.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void AddHobbiesGallery_RegistersGalleryDependenciesAsScoped()
    {
        var services = CreateServices();

        Assert.Equal(typeof(GalleryService), AssertScoped<IGalleryService>(services).ImplementationType);
        Assert.NotNull(AssertScoped<IGalleryRepository>(services).ImplementationFactory);
        Assert.Equal(typeof(HobbiesGalleryDbContext), AssertScoped<HobbiesGalleryDbContext>(services).ImplementationType);
        AssertScoped<DbContextOptions<HobbiesGalleryDbContext>>(services);
        Assert.Equal(typeof(DenyAllTestActorContext), AssertScoped<IGalleryActorContext>(services).ImplementationType);
    }

    [Fact]
    public void AddHobbiesGallery_ResolvesAndReusesDependenciesOnlyWithinEachScope()
    {
        using var provider = BuildProvider(CreateServices());
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGalleryService>());
        AssertScopedResolution<IGalleryService, GalleryService>(firstScope, secondScope);
        AssertScopedResolution<IGalleryRepository, GalleryRepository<HobbiesGalleryDbContext>>(firstScope, secondScope);
        AssertScopedResolution<IGalleryActorContext, DenyAllTestActorContext>(firstScope, secondScope);
        AssertScopedResolution<DbContextOptions<HobbiesGalleryDbContext>, DbContextOptions<HobbiesGalleryDbContext>>(
            firstScope, secondScope);
        var firstContext = AssertScopedResolution<HobbiesGalleryDbContext, HobbiesGalleryDbContext>(firstScope, secondScope);

        Assert.True(firstContext.Database.IsNpgsql());
        Assert.Equal(ConnectionState.Closed, firstContext.Database.GetDbConnection().State);
        Assert.Equal(ConnectionState.Closed,
            secondScope.ServiceProvider.GetRequiredService<HobbiesGalleryDbContext>().Database.GetDbConnection().State);
    }

    [Fact]
    public async Task AddHobbiesGallery_DoesNotComposeMediaAccessOrProductionAuthorization()
    {
        var services = CreateServices();
        // Apart from EF infrastructure, the extension must add only the Gallery composition services.
        var expected = new[] { typeof(HobbiesGalleryDbContext), typeof(IGalleryActorContext),
            typeof(IGalleryRepository), typeof(IGalleryService) };
        var applicationServices = services.Where(descriptor => descriptor.ServiceType.Assembly != typeof(DbContext).Assembly)
            .Select(descriptor => descriptor.ServiceType);
        Assert.Equal(expected.OrderBy(type => type.FullName, StringComparer.Ordinal),
            applicationServices.OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.DoesNotContain(services, descriptor =>
            typeof(IGalleryPrivateMediaAccessReader).IsAssignableFrom(descriptor.ServiceType)
            || descriptor.ImplementationType is { } type && typeof(IGalleryPrivateMediaAccessReader).IsAssignableFrom(type));

        using var provider = BuildProvider(services);
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider;
        Assert.Null(scoped.GetService<IGalleryPrivateMediaAccessReader>());
        Assert.Null(scoped.GetService<IHobbiesGalleryProfileAccessReader>());
        Assert.Null(scoped.GetService<AccountBackedGalleryActorResolver>());
        Assert.Null(scoped.GetRequiredService<IGalleryActorContext>().Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scoped.GetRequiredService<IGalleryService>().GetMineAsync());
        Assert.Equal(ConnectionState.Closed, scoped.GetRequiredService<HobbiesGalleryDbContext>().Database.GetDbConnection().State);
    }

    private static IServiceCollection CreateServices() =>
        new ServiceCollection().AddHobbiesGallery<DenyAllTestActorContext>(options =>
            options.UseNpgsql("Host=127.0.0.1;Database=andril_gallery_composition;Username=unused",
                    HobbiesDatabaseOptions.ConfigureGalleryHistory)
                .AddInterceptors(new RejectConnectionOpen()));

    private static ServiceProvider BuildProvider(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    private static ServiceDescriptor AssertScoped<TService>(IServiceCollection services)
    {
        var registration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        return registration;
    }

    private static TImplementation AssertScopedResolution<TService, TImplementation>(IServiceScope first, IServiceScope second)
        where TService : class
        where TImplementation : class, TService
    {
        var firstInstance = Assert.IsType<TImplementation>(first.ServiceProvider.GetRequiredService<TService>());
        Assert.Same(firstInstance, first.ServiceProvider.GetRequiredService<TService>());
        var secondInstance = Assert.IsType<TImplementation>(second.ServiceProvider.GetRequiredService<TService>());
        Assert.Same(secondInstance, second.ServiceProvider.GetRequiredService<TService>());
        Assert.NotSame(firstInstance, secondInstance);
        return firstInstance;
    }

    private sealed class DenyAllTestActorContext : IGalleryActorContext
    {
        public GalleryActor? Current => null;
    }

    private sealed class RejectConnectionOpen : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }

        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            Reject();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Reject());

        private InterceptionResult Reject()
        {
            Attempts++;
            throw new InvalidOperationException("Gallery DI composition must stay offline.");
        }
    }
}
