using System.Reflection;
using Andril.API.Authorization.Groups;
using Andril.API.Controllers.Groups;
using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Groups;

public sealed class GroupActorContextServiceRegistrationTests
{
    [Fact]
    public void Registration_IsExplicitScopedIdempotentAndDoesNotInventDependencies()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddGroupActorContextServices());
        var descriptors = services.ToArray();
        services.AddGroupActorContextServices();
        Assert.Equal(descriptors, services.ToArray());
        Assert.Equal(2, services.Count);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IGroupActorContext)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(GroupActorContext)).Lifetime);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPrivateProfileService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupAuthoritativeEvidenceProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(TimeProvider));
        Assert.Throws<ArgumentNullException>(() => GroupActorContextServiceCollectionExtensions.AddGroupActorContextServices(null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingMandatoryDependency_FailsResolutionForBothServiceTypes(bool missingProfiles)
    {
        var services = new ServiceCollection();
        if (missingProfiles) services.AddScoped<IGroupAuthoritativeEvidenceProvider, EvidenceDouble>();
        else services.AddScoped<IPrivateProfileService, ProfileDouble>();
        services.AddGroupActorContextServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var expected = missingProfiles ? nameof(IPrivateProfileService) : nameof(IGroupAuthoritativeEvidenceProvider);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IGroupActorContext>()).Message);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GroupActorContext>()).Message);
    }

    [Fact]
    public async Task ExplicitDependencies_ResolveSameScopedActorAndPreserveTrustedTimeProvider()
    {
        var services = Dependencies();
        services.AddGroupActorContextServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var actor = first.ServiceProvider.GetRequiredService<IGroupActorContext>();
        Assert.Same(actor, first.ServiceProvider.GetRequiredService<GroupActorContext>());
        Assert.Same(actor, first.ServiceProvider.GetRequiredService<IGroupActorContext>());
        Assert.NotSame(actor, second.ServiceProvider.GetRequiredService<IGroupActorContext>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGroupActorContext>());

        var evidence = await actor.GetCurrentActorAsync("social");
        Assert.NotNull(evidence);
        Assert.Equal(ProfileDouble.ProfileId, evidence.ProfileId);
        Assert.Equal(ClockDouble.Now, evidence.EvidenceTimestampUtc);
        var clock = Assert.IsType<ClockDouble>(first.ServiceProvider.GetRequiredService<TimeProvider>());
        clock.Advance();
        Assert.Null(await actor.GetCurrentActorAsync("social"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CompatibleExistingRegistration_IsPreservedWithSharedScopedIdentity(bool interfaceRegistration, bool factory)
    {
        var services = Dependencies();
        var type = interfaceRegistration ? typeof(IGroupActorContext) : typeof(GroupActorContext);
        var original = factory
            ? ServiceDescriptor.Scoped(type, sp => new GroupActorContext(sp.GetRequiredService<IPrivateProfileService>(),
                sp.GetRequiredService<IGroupAuthoritativeEvidenceProvider>(), sp.GetRequiredService<TimeProvider>()))
            : ServiceDescriptor.Scoped(type, typeof(GroupActorContext));
        ((IServiceCollection)services).Add(original);
        services.AddGroupActorContextServices();
        services.AddGroupActorContextServices();
        Assert.Same(original, Assert.Single(services, d => d.ServiceType == type));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<IGroupActorContext>(),
            scope.ServiceProvider.GetRequiredService<GroupActorContext>());
    }

    [Theory]
    [InlineData("singleton")]
    [InlineData("transient")]
    [InlineData("incompatible")]
    [InlineData("duplicates")]
    [InlineData("independent")]
    public void IncompatibleDescriptors_AreRejectedWithoutChangingExistingRegistrations(string kind)
    {
        var services = Dependencies();
        if (kind == "singleton") services.AddSingleton<IGroupActorContext, IncompatibleActorDouble>();
        if (kind == "transient") services.AddTransient<GroupActorContext>();
        if (kind == "incompatible") services.AddScoped<IGroupActorContext, IncompatibleActorDouble>();
        if (kind == "duplicates")
        {
            services.AddScoped<IGroupActorContext, GroupActorContext>();
            services.AddScoped<IGroupActorContext, GroupActorContext>();
        }
        if (kind == "independent")
        {
            services.AddScoped<IGroupActorContext, GroupActorContext>();
            services.AddScoped<GroupActorContext>();
        }
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddGroupActorContextServices());
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void IncompatibleOpaqueFactory_RejectsConcreteResolutionInsteadOfCreatingFallbackActor()
    {
        var services = Dependencies();
        services.AddScoped<IGroupActorContext>(_ => new IncompatibleActorDouble());
        services.AddGroupActorContextServices();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GroupActorContext>());
    }

    [Fact]
    public void DefaultHost_DoesNotOptInOrActivateGroupController()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Andril.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var program = File.ReadAllText(Path.Combine(directory.FullName, "src", "Andril.API", "Program.cs"));
        Assert.DoesNotContain("AddGroupActorContextServices", program);
        Assert.DoesNotContain("AddGroupMembershipServices", program);
        Assert.NotNull(typeof(GroupMembershipController).GetCustomAttribute<NonControllerAttribute>());
    }

    private static ServiceCollection Dependencies()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPrivateProfileService, ProfileDouble>();
        services.AddScoped<IGroupAuthoritativeEvidenceProvider, EvidenceDouble>();
        services.AddSingleton<TimeProvider>(new ClockDouble());
        return services;
    }

    // Explicit test doubles only; never registered by production code.
    private sealed class ProfileDouble : IPrivateProfileService
    {
        public static readonly Guid ProfileId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken = default) => action(new(ProfileId, module, ClockDouble.Now));
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EvidenceDouble : IGroupAuthoritativeEvidenceProvider
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(Guid profileId, string module,
            CancellationToken cancellationToken = default) => Task.FromResult<AuthoritativeGroupProfileEvidence?>(new(profileId, module, true, false, 25, ClockDouble.Now));
        public Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsGroupOwnerAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> GetCurrentMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int?> GetGroupCapacityAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ClockDouble : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now += TimeSpan.FromHours(1);
    }

    private sealed class IncompatibleActorDouble : IGroupActorContext
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string expectedModule,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
