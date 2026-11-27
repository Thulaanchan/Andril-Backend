using Andril.API.Authorization.Groups;
using Andril.API.Extensions;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Groups;

public sealed class GroupDiscoveryServiceRegistrationTests
{
    private static IServiceCollection Dependencies()
        => new ServiceCollection().AddScoped<IGroupDiscoveryReadRepository, RepositoryDouble>()
            .AddScoped<IGroupActorContext, ActorDouble>();

    [Fact]
    public void OptIn_IsScopedIdempotentAndDoesNotFabricateDependencies()
    {
        IServiceCollection services = new ServiceCollection();
        Assert.Same(services, services.AddGroupDiscoveryServices());
        var before = services.ToArray();
        services.AddGroupDiscoveryServices();
        Assert.Equal(before, services.ToArray());
        Assert.Equal(3, services.Count);
        Assert.All(services, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGroupDiscoveryReadRepository));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGroupActorContext));
        Assert.Throws<ArgumentNullException>(() => GroupDiscoveryServiceCollectionExtensions.AddGroupDiscoveryServices(null!));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void MissingDependency_FailsClosedDuringAccessResolution(bool missingRepository)
    {
        IServiceCollection services = new ServiceCollection();
        if (missingRepository) services.AddScoped<IGroupActorContext, ActorDouble>();
        else services.AddScoped<IGroupDiscoveryReadRepository, RepositoryDouble>();
        services.AddGroupDiscoveryServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var expected = missingRepository ? nameof(IGroupDiscoveryReadRepository) : nameof(IGroupActorContext);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IGroupDiscoveryAccessService>()).Message);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GroupDiscoveryAccessService>()).Message);
    }

    [Fact]
    public void ValidComposition_HasSharedAccessIdentityAndIndependentRequestScopes()
    {
        var services = Dependencies().AddGroupDiscoveryServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var access = first.ServiceProvider.GetRequiredService<IGroupDiscoveryAccessService>();
        Assert.Same(access, first.ServiceProvider.GetRequiredService<GroupDiscoveryAccessService>());
        Assert.Same(access, first.ServiceProvider.GetRequiredService<IGroupDiscoveryAccessService>());
        Assert.NotSame(access, second.ServiceProvider.GetRequiredService<IGroupDiscoveryAccessService>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<GroupDiscoveryService>(), second.ServiceProvider.GetRequiredService<GroupDiscoveryService>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGroupDiscoveryAccessService>());
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void ExistingCompatibleAccessFactoriesAndApplicationRegistration_ArePreserved(bool interfaceFactory)
    {
        var services = Dependencies();
        var application = ServiceDescriptor.Scoped<GroupDiscoveryService>(sp => new(sp.GetRequiredService<IGroupDiscoveryReadRepository>()));
        services.Add(application);
        var type = interfaceFactory ? typeof(IGroupDiscoveryAccessService) : typeof(GroupDiscoveryAccessService);
        var access = ServiceDescriptor.Scoped(type, sp => new GroupDiscoveryAccessService(
            sp.GetRequiredService<IGroupActorContext>(), sp.GetRequiredService<GroupDiscoveryService>()));
        services.Add(access);
        services.AddGroupDiscoveryServices();
        services.AddGroupDiscoveryServices();
        Assert.Same(application, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GroupDiscoveryService)));
        Assert.Same(access, Assert.Single(services, descriptor => descriptor.ServiceType == type));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<IGroupDiscoveryAccessService>(), scope.ServiceProvider.GetRequiredService<GroupDiscoveryAccessService>());
    }

    [Fact]
    public void InvalidLifetimeOrDuplicateDescriptors_AreRejectedWithoutMutation()
    {
        foreach (var duplicate in new[] { false, true })
        {
            var services = Dependencies();
            if (duplicate)
            {
                services.AddScoped<GroupDiscoveryService>();
                services.AddScoped<GroupDiscoveryService>();
            }
            else services.AddSingleton<GroupDiscoveryService>();
            var before = services.ToArray();
            Assert.Throws<InvalidOperationException>(() => services.AddGroupDiscoveryServices());
            Assert.Equal(before, services.ToArray());
        }
    }

    [Fact]
    public void DefaultHost_DoesNotActivateDiscoveryComposition()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Andril.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var program = File.ReadAllText(Path.Combine(directory.FullName, "src", "Andril.API", "Program.cs"));
        Assert.DoesNotContain("AddGroupDiscoveryServices", program);
        Assert.DoesNotContain("GroupDiscoveryAccessService", program);
    }

    private sealed class ActorDouble : IGroupActorContext
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string expectedModule, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthoritativeGroupProfileEvidence?>(null);
    }
    private sealed class RepositoryDouble : IGroupDiscoveryReadRepository
    {
        public Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
            Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException("Composition test double only.");
        public Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query, GroupDiscoverySnapshot snapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(GroupDiscoveryDecision.Unknown);
    }
}
