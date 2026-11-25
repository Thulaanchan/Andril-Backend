using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Controllers.Social;
using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Social;

/// <summary>
/// Verifies dependency injection registration security, lifetime integrity,
/// fail-closed behavior, and production host boundaries for Social discovery services (SOC-02).
/// </summary>
public sealed class SocialDiscoveryServiceRegistrationTests
{
    #region A. Descriptor & Lifetime Verification

    [Fact]
    public void AddSocialDiscoveryServices_RegistersExpectedDescriptors_WithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();

        var interfaceDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialDiscoveryAccessService));
        Assert.NotNull(interfaceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, interfaceDescriptor.Lifetime);
        Assert.Equal(typeof(SocialDiscoveryAccessService), interfaceDescriptor.ImplementationType);

        var concreteDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SocialDiscoveryAccessService));
        Assert.NotNull(concreteDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, concreteDescriptor.Lifetime);
        Assert.Equal(typeof(SocialDiscoveryAccessService), concreteDescriptor.ImplementationType);
    }

    [Fact]
    public void AddSocialDiscoveryServices_CalledRepeatedly_DoesNotIntroduceDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddSocialDiscoveryServices();
        services.AddSocialDiscoveryServices();

        var interfaceDescriptors = services.Where(d => d.ServiceType == typeof(ISocialDiscoveryAccessService)).ToList();
        Assert.Single(interfaceDescriptors);

        var concreteDescriptors = services.Where(d => d.ServiceType == typeof(SocialDiscoveryAccessService)).ToList();
        Assert.Single(concreteDescriptors);
    }

    [Fact]
    public void AddSocialDiscoveryServices_PreservesPreExistingRegistrations()
    {
        var services = new ServiceCollection();
        var customInstance = new StubSocialDiscoveryAccessService();
        services.AddScoped<ISocialDiscoveryAccessService>(_ => customInstance);

        services.AddSocialDiscoveryServices();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialDiscoveryAccessService));
        Assert.NotNull(descriptor);
        Assert.Null(descriptor.ImplementationType); // Factory was preserved

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>();
        Assert.Same(customInstance, resolved);
    }

    [Fact]
    public void AddSocialDiscoveryServices_DoesNotRegisterFakeProductionEvidenceProviderOrDbContext()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialDiscoveryEvidenceProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialDbContext));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialExploreController));
    }

    #endregion

    #region B. Fail-Closed Dependency Resolution

    [Fact]
    public void AddSocialDiscoveryServices_ResolvesSuccessfully_WhenDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialDiscoveryEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>();
        Assert.NotNull(service);
        Assert.IsType<SocialDiscoveryAccessService>(service);

        var concrete = scope.ServiceProvider.GetRequiredService<SocialDiscoveryAccessService>();
        Assert.NotNull(concrete);
    }

    [Fact]
    public void AddSocialDiscoveryServices_MissingProfileService_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddScoped<ISocialDiscoveryEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>());
    }

    [Fact]
    public void AddSocialDiscoveryServices_MissingEvidenceProvider_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>());
    }

    [Fact]
    public void AddSocialDiscoveryServices_MissingAllDependencies_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>());
    }

    #endregion

    #region C. Container Validation & Scope Integrity

    [Fact]
    public void AddSocialDiscoveryServices_StrictContainerValidation_PassesWithSuppliedDependencies()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialDiscoveryEvidenceProvider, StubEvidenceProvider>();

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<ISocialDiscoveryAccessService>();
        Assert.NotNull(resolved);
    }

    [Fact]
    public void AddSocialDiscoveryServices_CannotResolveFromRootProvider_WhenScoped()
    {
        var services = new ServiceCollection();
        services.AddSocialDiscoveryServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialDiscoveryEvidenceProvider, StubEvidenceProvider>();

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ISocialDiscoveryAccessService>());
    }

    #endregion

    #region D. Production Host Non-Activation & Controller Discovery Boundaries

    [Fact]
    public void DefaultHost_DoesNotInvokeSocialDiscoveryRegistration()
    {
        var programSource = ReadProgramSource();

        Assert.DoesNotContain("AddSocialDiscoveryServices", programSource);
    }

    [Fact]
    public void SocialExploreController_IsDecoratedWithNonControllerAttribute()
    {
        var attribute = typeof(SocialExploreController).GetCustomAttribute<NonControllerAttribute>(inherit: false);
        Assert.NotNull(attribute);
    }

    #endregion

    #region Helpers & Test Doubles

    private static string ReadProgramSource()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Andril.API",
                "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("ANDRIL repository source root was not found.");
    }

    private sealed class StubProfileService : IPrivateProfileService
    {
        private readonly PrivateProfileDto _profile = new(Guid.NewGuid(), "social", DateTimeOffset.UtcNow)
        {
            DisplayName = "TestUser"
        };

        public Task<T> WithAuthorizedProfileAsync<T>(
            string module,
            Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken = default) =>
            action(_profile);

        public Task<PrivateProfileDto> GetAsync(
            string module,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<PrivateProfileDto> CreateAsync(
            string module,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class StubEvidenceProvider : ISocialDiscoveryEvidenceProvider
    {
        public Task<SocialDiscoveryCallerBundle?> GetCallerBundleAsync(
            Guid callerProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SocialDiscoveryCallerBundle?>(null);

        public Task<IReadOnlyList<SocialAuthoritativeCandidateItem>> GetCandidateBatchAsync(
            Guid callerProfileId,
            int maxCandidateBatchSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SocialAuthoritativeCandidateItem>>(Array.Empty<SocialAuthoritativeCandidateItem>());
    }

    private sealed class StubSocialDiscoveryAccessService : ISocialDiscoveryAccessService
    {
        public Task<IReadOnlyList<SocialExploreCandidateCard>> DiscoverCandidatesAsync(
            int rotationOffset = 0,
            int limit = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SocialExploreCandidateCard>>(Array.Empty<SocialExploreCandidateCard>());
    }

    #endregion
}
