using System;
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
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Social;

/// <summary>
/// Verifies dependency injection registration security, lifetime integrity,
/// fail-closed behavior, and production host boundaries for Social relationship services (CF-20).
/// </summary>
public sealed class SocialRelationshipServiceRegistrationTests
{
    #region A. Descriptor & Lifetime Verification

    [Fact]
    public void AddSocialRelationshipServices_RegistersExpectedDescriptors_WithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();

        var interfaceDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialRelationshipAccessService));
        Assert.NotNull(interfaceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, interfaceDescriptor.Lifetime);
        Assert.Equal(typeof(SocialRelationshipAccessService), interfaceDescriptor.ImplementationType);

        var concreteDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SocialRelationshipAccessService));
        Assert.NotNull(concreteDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, concreteDescriptor.Lifetime);
        Assert.Equal(typeof(SocialRelationshipAccessService), concreteDescriptor.ImplementationType);
    }

    [Fact]
    public void AddSocialRelationshipServices_CalledRepeatedly_DoesNotIntroduceDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddSocialRelationshipServices();
        services.AddSocialRelationshipServices();

        var interfaceDescriptors = services.Where(d => d.ServiceType == typeof(ISocialRelationshipAccessService)).ToList();
        Assert.Single(interfaceDescriptors);

        var concreteDescriptors = services.Where(d => d.ServiceType == typeof(SocialRelationshipAccessService)).ToList();
        Assert.Single(concreteDescriptors);
    }

    [Fact]
    public void AddSocialRelationshipServices_PreservesPreExistingRegistrations()
    {
        var services = new ServiceCollection();
        var customInstance = new StubSocialRelationshipAccessService();
        services.AddScoped<ISocialRelationshipAccessService>(_ => customInstance);

        services.AddSocialRelationshipServices();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialRelationshipAccessService));
        Assert.NotNull(descriptor);
        Assert.Null(descriptor.ImplementationType); // Factory preserved

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        Assert.Same(customInstance, resolved);
    }

    [Fact]
    public void AddSocialRelationshipServices_DoesNotRegisterFakeTransactionRepositoryOrDbContext()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialRelationshipTransactionRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialRelationshipEvidenceProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialDbContext));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialRelationshipsController));
    }

    #endregion

    #region B. Fail-Closed Dependency Resolution

    [Fact]
    public void AddSocialRelationshipServices_ResolvesSuccessfully_WhenDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        Assert.NotNull(service);
        Assert.IsType<SocialRelationshipAccessService>(service);

        var concrete = scope.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>();
        Assert.NotNull(concrete);
        Assert.Same(service, concrete);
    }

    [Fact]
    public void AddSocialRelationshipServices_RepeatedResolutionWithinScope_PreservesIdentity()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var s1 = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        var s2 = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        var c1 = scope.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>();
        var c2 = scope.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>();

        Assert.Same(s1, s2);
        Assert.Same(s1, c1);
        Assert.Same(c1, c2);
    }

    [Fact]
    public void AddSocialRelationshipServices_SeparateRequestScopes_ReceiveSeparateInstances()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var instance1 = scope1.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        var instance2 = scope2.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();

        Assert.NotNull(instance1);
        Assert.NotNull(instance2);
        Assert.NotSame(instance1, instance2);

        var concrete1 = scope1.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>();
        var concrete2 = scope2.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>();

        Assert.Same(instance1, concrete1);
        Assert.Same(instance2, concrete2);
    }

    [Fact]
    public void AddSocialRelationshipServices_IncompatibleCustomInterface_FailsSafelyWithInvalidOperationException()
    {
        var services = new ServiceCollection();
        var customMock = new StubSocialRelationshipAccessService();
        services.AddScoped<ISocialRelationshipAccessService>(_ => customMock);
        services.AddSocialRelationshipServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvedInterface = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        Assert.Same(customMock, resolvedInterface);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>());

        Assert.Contains("is not compatible with concrete SocialRelationshipAccessService resolution", ex.Message);
    }

    [Fact]
    public void AddSocialRelationshipServices_IncompatibleCustomInterface_DoesNotBypassCustomAuthorization()
    {
        var services = new ServiceCollection();
        var customMock = new StubSocialRelationshipAccessService();
        services.AddScoped<ISocialRelationshipAccessService>(_ => customMock);
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<SocialRelationshipAccessService>());
    }

    [Fact]
    public void AddSocialRelationshipServices_MissingProfileService_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>());
    }

    [Fact]
    public void AddSocialRelationshipServices_MissingTransactionRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>());
    }

    [Fact]
    public void AddSocialRelationshipServices_MissingEvidenceProvider_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>());
    }

    [Fact]
    public void AddSocialRelationshipServices_MissingAllDependencies_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>());
    }

    #endregion

    #region C. Container Validation & Scope Integrity

    [Fact]
    public void AddSocialRelationshipServices_StrictContainerValidation_PassesWithSuppliedDependencies()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<ISocialRelationshipAccessService>();
        Assert.NotNull(resolved);
    }

    [Fact]
    public void AddSocialRelationshipServices_CannotResolveFromRootProvider_WhenScoped()
    {
        var services = new ServiceCollection();
        services.AddSocialRelationshipServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<ISocialRelationshipTransactionRepository, StubTransactionRepository>();
        services.AddScoped<ISocialRelationshipEvidenceProvider, StubEvidenceProvider>();

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ISocialRelationshipAccessService>());
    }

    #endregion

    #region D. Production Host Non-Activation & Controller Discovery Boundaries

    [Fact]
    public void DefaultHost_DoesNotInvokeSocialRelationshipRegistration()
    {
        var programSource = ReadProgramSource();

        Assert.DoesNotContain("AddSocialRelationshipServices", programSource);
    }

    [Fact]
    public void SocialRelationshipsController_IsDecoratedWithNonControllerAttribute()
    {
        var attribute = typeof(SocialRelationshipsController).GetCustomAttribute<NonControllerAttribute>(inherit: false);
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

    private sealed class StubEvidenceProvider : ISocialRelationshipEvidenceProvider
    {
        public Task<SocialDiscoveryCallerEvidence?> GetCallerEvidenceAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SocialDiscoveryCallerEvidence?>(null);

        public Task<SocialDiscoveryCandidateEvidence?> GetCandidateEvidenceAsync(
            Guid candidateProfileId,
            Guid callerProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SocialDiscoveryCandidateEvidence?>(null);
    }

    private sealed class StubTransactionRepository : ISocialRelationshipTransactionRepository
    {
        public Task<SocialRelationshipSnapshot> GetRelationshipStateAsync(
            SocialProfilePairKey pairKey,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SocialRelationshipSnapshot(pairKey, SocialRelationshipState.None));

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SocialDirectionalInterest?>(null);

        public Task<SocialDirectionalInterest?> GetReciprocalInterestAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SocialDirectionalInterest?>(null);

        public Task<SocialInterestTransactionResult> RecordInterestAndTryMatchAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            bool isPositive,
            DateTime timestampUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.PendingUnilateral));

        public Task<SocialRevocationTransactionResult> RevokeMatchAsync(
            SocialProfilePairKey pairKey,
            Guid actingProfileId,
            DateTime timestampUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SocialRevocationTransactionResult(success: true, matchRevoked: true, revokedMatchId: Guid.NewGuid(), revokedAtUtc: DateTime.UtcNow));
    }

    private sealed class StubSocialRelationshipAccessService : ISocialRelationshipAccessService
    {
        public Task<SocialRelationshipOperationResult> RecordInterestAsync(
            Guid targetProfileId,
            bool isPositive,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialRelationshipOperationResult.InterestRecorded(SocialRelationshipState.PendingUnilateral));

        public Task<SocialRelationshipStatusResult> GetRelationshipStatusAsync(
            Guid targetProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SocialRelationshipStatusResult.NotMatched());

        public Task<SocialRevocationOperationResult> RevokeMatchAsync(
            Guid targetProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SocialRevocationOperationResult(success: true, matchRevoked: true));
    }

    #endregion
}
