using Andril.API.Authorization.Groups;
using Andril.API.Extensions;
using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Groups;

public sealed class GroupPersonalParticipationServiceRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.NewGuid();

    [Fact]
    public void ExplicitOptInIsScopedIdempotentAndSuppliesNoAuthority()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddGroupPersonalParticipationServices());
        var registrations = services.ToArray();
        Assert.Same(services, services.AddGroupPersonalParticipationServices());
        Assert.Equal(registrations, services.ToArray());
        Assert.Equal(3, registrations.Length);
        Assert.All(registrations, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGroupPersonalParticipationReadRepository));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGroupActorContext));
        Assert.Throws<ArgumentNullException>(() =>
            GroupPersonalParticipationServiceCollectionExtensions.AddGroupPersonalParticipationServices(null!));
    }

    [Fact]
    public async Task ValidCompositionResolvesOneAccessServicePerScopeAndUsesSuppliedAuthority()
    {
        var services = Dependencies().AddGroupPersonalParticipationServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var access = first.ServiceProvider.GetRequiredService<IGroupPersonalParticipationAccessService>();
        var application = first.ServiceProvider.GetRequiredService<GroupPersonalParticipationService>();

        Assert.Same(access, first.ServiceProvider.GetRequiredService<GroupPersonalParticipationAccessService>());
        Assert.Same(access, first.ServiceProvider.GetRequiredService<IGroupPersonalParticipationAccessService>());
        Assert.NotSame(access, second.ServiceProvider.GetRequiredService<IGroupPersonalParticipationAccessService>());
        Assert.Same(application, first.ServiceProvider.GetRequiredService<GroupPersonalParticipationService>());
        Assert.NotSame(application, second.ServiceProvider.GetRequiredService<GroupPersonalParticipationService>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGroupPersonalParticipationAccessService>());

        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var result = await access.GetStatusAsync(scope, groupId);
        Assert.NotNull(result);
        Assert.Equal(groupId, result.GroupId);
        Assert.Equal(GroupPersonalParticipationState.None, result.State);
        var repository = Assert.IsType<RepositoryDouble>(first.ServiceProvider.GetRequiredService<IGroupPersonalParticipationReadRepository>());
        Assert.Equal(new GroupPersonalParticipationQuery(ActorId, groupId, scope), repository.Query);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingAuthorityFailsClosedDuringAccessResolution(bool missingRepository)
    {
        var services = new ServiceCollection();
        if (missingRepository) services.AddScoped<IGroupActorContext, ActorDouble>();
        else services.AddScoped<IGroupPersonalParticipationReadRepository, RepositoryDouble>();
        services.AddGroupPersonalParticipationServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var expected = missingRepository ? nameof(IGroupPersonalParticipationReadRepository) : nameof(IGroupActorContext);

        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupPersonalParticipationAccessService>()).Message);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<GroupPersonalParticipationAccessService>()).Message);
        if (missingRepository)
            Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<GroupPersonalParticipationService>()).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompatibleCustomRegistrationsArePreservedWithOneScopedIdentity(bool interfaceFactory)
    {
        var services = Dependencies();
        var application = ServiceDescriptor.Scoped<GroupPersonalParticipationService>(sp =>
            new GroupPersonalParticipationService(sp.GetRequiredService<IGroupPersonalParticipationReadRepository>(),
                sp.GetRequiredService<TimeProvider>()));
        services.Add(application);
        var serviceType = interfaceFactory ? typeof(IGroupPersonalParticipationAccessService) : typeof(GroupPersonalParticipationAccessService);
        var access = ServiceDescriptor.Scoped(serviceType, sp => new GroupPersonalParticipationAccessService(
            sp.GetRequiredService<IGroupActorContext>(), sp.GetRequiredService<GroupPersonalParticipationService>(),
            sp.GetRequiredService<TimeProvider>()));
        services.Add(access);

        services.AddGroupPersonalParticipationServices().AddGroupPersonalParticipationServices();
        Assert.Same(application, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GroupPersonalParticipationService)));
        Assert.Same(access, Assert.Single(services, descriptor => descriptor.ServiceType == serviceType));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<IGroupPersonalParticipationAccessService>(),
            scope.ServiceProvider.GetRequiredService<GroupPersonalParticipationAccessService>());
    }

    [Theory]
    [InlineData("singleton")]
    [InlineData("duplicate")]
    [InlineData("different-type")]
    [InlineData("independent-aliases")]
    public void RejectsIncompatibleRegistrationWithoutMutatingCollection(string incompatible)
    {
        var services = Dependencies();
        switch (incompatible)
        {
            case "singleton":
                services.AddSingleton<GroupPersonalParticipationService>();
                break;
            case "duplicate":
                services.AddScoped<GroupPersonalParticipationService>();
                services.AddScoped<GroupPersonalParticipationService>();
                break;
            case "different-type":
                services.AddScoped<IGroupPersonalParticipationAccessService, OtherAccess>();
                break;
            case "independent-aliases":
                services.AddScoped<IGroupPersonalParticipationAccessService, GroupPersonalParticipationAccessService>();
                services.AddScoped<GroupPersonalParticipationAccessService>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(incompatible));
        }
        var before = services.ToArray();

        Assert.Throws<InvalidOperationException>(() => services.AddGroupPersonalParticipationServices());
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void IncompatibleCustomFactoryFailsConcreteResolution()
    {
        var services = Dependencies();
        services.AddScoped<IGroupPersonalParticipationAccessService>(_ => new OtherAccess());
        services.AddGroupPersonalParticipationServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GroupPersonalParticipationAccessService>());
    }

    [Fact]
    public void DefaultHostDoesNotActivateParticipationComposition()
    {
        using var host = new WebApplicationFactory<Program>();
        using var scope = host.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IGroupPersonalParticipationAccessService>());
        Assert.Null(scope.ServiceProvider.GetService<GroupPersonalParticipationAccessService>());
        Assert.Null(scope.ServiceProvider.GetService<GroupPersonalParticipationService>());
        Assert.Null(scope.ServiceProvider.GetService<IGroupPersonalParticipationReadRepository>());
    }

    private static IServiceCollection Dependencies() => new ServiceCollection()
        .AddScoped<IGroupPersonalParticipationReadRepository, RepositoryDouble>()
        .AddScoped<IGroupActorContext, ActorDouble>()
        .AddSingleton<TimeProvider, Clock>();

    private sealed class OtherAccess : IGroupPersonalParticipationAccessService
    {
        public Task<GroupPersonalParticipationDto?> GetStatusAsync(GroupModuleScope scope, Guid groupId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ActorDouble : IGroupActorContext
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string expectedModule, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthoritativeGroupProfileEvidence?>(new(ActorId, expectedModule, true, false, 25, Now));
    }

    private sealed class RepositoryDouble : IGroupPersonalParticipationReadRepository
    {
        public GroupPersonalParticipationQuery? Query { get; private set; }

        public Task<T?> WithAuthorizedReadAsync<T>(GroupPersonalParticipationQuery query,
            Func<GroupPersonalParticipationSnapshot, CancellationToken, Task<T?>> project,
            CancellationToken cancellationToken = default) where T : class
        {
            const string revision = "composition-test-read";
            const GroupPersonalParticipationDecision allowed = GroupPersonalParticipationDecision.Allowed;
            Query = query;
            return project(new GroupPersonalParticipationSnapshot(query.ActorProfileId, query.GroupId, query.Scope,
                allowed, allowed, allowed, Now, revision, new(GroupPersonalParticipationPresence.Absent, null, revision),
                new(GroupPersonalParticipationPresence.Absent, null, revision)), cancellationToken);
        }

        public Task<GroupPersonalParticipationDecision> RevalidateAsync(GroupPersonalParticipationQuery query,
            GroupPersonalParticipationSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.FromResult(GroupPersonalParticipationDecision.Allowed);
    }
}
