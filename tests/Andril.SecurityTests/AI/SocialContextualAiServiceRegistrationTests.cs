using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Recommendations;
using Andril.AI.Services;
using Andril.API.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>
/// Verifies dependency injection registration security, lifetime integrity,
/// fail-closed resolution boundaries, container validation, and default host non-activation
/// for Social contextual AI consent and recommendation services (CF-18).
/// </summary>
public sealed class SocialContextualAiServiceRegistrationTests
{
    #region A. Descriptor & Lifetime Verification

    [Fact]
    public void AddSocialContextualAiServices_RegistersExpectedDescriptors_WithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();

        var guardDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(AiPrivacyNoticeGuard));
        Assert.NotNull(guardDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, guardDescriptor.Lifetime);
        Assert.Equal(typeof(AiPrivacyNoticeGuard), guardDescriptor.ImplementationType);

        var consentDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(AiContextualConsentService));
        Assert.NotNull(consentDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, consentDescriptor.Lifetime);
        Assert.Equal(typeof(AiContextualConsentService), consentDescriptor.ImplementationType);

        var recommendationDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(AiContextualRecommendationService));
        Assert.NotNull(recommendationDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, recommendationDescriptor.Lifetime);
        Assert.Equal(typeof(AiContextualRecommendationService), recommendationDescriptor.ImplementationType);
    }

    [Fact]
    public void AddSocialContextualAiServices_CalledRepeatedly_DoesNotIntroduceDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        services.AddSocialContextualAiServices();
        services.AddSocialContextualAiServices();

        Assert.Single(services, d => d.ServiceType == typeof(AiPrivacyNoticeGuard));
        Assert.Single(services, d => d.ServiceType == typeof(AiContextualConsentService));
        Assert.Single(services, d => d.ServiceType == typeof(AiContextualRecommendationService));
    }

    [Fact]
    public void AddSocialContextualAiServices_PreservesPreExistingRegistrations()
    {
        var services = new ServiceCollection();
        RegisterAllFourDependencies(services);

        var customGuard = new AiPrivacyNoticeGuard(
            services.BuildServiceProvider().GetRequiredService<IAiActorAuthorization>(),
            services.BuildServiceProvider().GetRequiredService<IAiPrivacyNoticeGate>());

        services.AddScoped(_ => customGuard);
        services.AddSocialContextualAiServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<AiPrivacyNoticeGuard>();
        Assert.Same(customGuard, resolved);
    }

    [Fact]
    public void AddSocialContextualAiServices_DoesNotRegisterFakeGeneratorsOrConsentRepositories()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiActorAuthorization));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiPrivacyNoticeGate));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiContextualConsentRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiContextualRecommendationGenerator));
    }

    #endregion

    #region B. Fail-Closed Dependency Resolution

    [Fact]
    public void AddSocialContextualAiServices_ResolvesSuccessfully_WhenAllDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var guard = scope.ServiceProvider.GetRequiredService<AiPrivacyNoticeGuard>();
        Assert.NotNull(guard);

        var consent = scope.ServiceProvider.GetRequiredService<AiContextualConsentService>();
        Assert.NotNull(consent);

        var recommendation = scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>();
        Assert.NotNull(recommendation);
    }

    [Fact]
    public void AddSocialContextualAiServices_MissingActorAuthorization_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IAiActorAuthorization)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>());
    }

    [Fact]
    public void AddSocialContextualAiServices_MissingPrivacyNoticeGate_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IAiPrivacyNoticeGate)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>());
    }

    [Fact]
    public void AddSocialContextualAiServices_MissingConsentRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IAiContextualConsentRepository)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>());
    }

    [Fact]
    public void AddSocialContextualAiServices_MissingRecommendationGenerator_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IAiContextualRecommendationGenerator)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>());
    }

    [Fact]
    public void AddSocialContextualAiServices_MissingAllDependencies_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>());
    }

    #endregion

    #region C. Container Validation & Scope Integrity

    [Fact]
    public void AddSocialContextualAiServices_StrictContainerValidation_PassesWithSuppliedDependencies()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<AiContextualRecommendationService>();
        Assert.NotNull(service);
    }

    [Fact]
    public void AddSocialContextualAiServices_CannotResolveFromRootProvider_WhenScoped()
    {
        var services = new ServiceCollection();
        services.AddSocialContextualAiServices();
        RegisterAllFourDependencies(services);

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<AiContextualRecommendationService>());
    }

    [Fact]
    public void AddSocialContextualAiServices_PreservesDisabledAiFoundation_WhenCoexisting()
    {
        var services = new ServiceCollection();
        services.AddDisabledAiFoundation();
        services.AddSocialContextualAiServices();

        // Foundation singleton registered
        Assert.Contains(services, d => d.ServiceType == typeof(IAiPrivacyNoticeGate) && d.Lifetime == ServiceLifetime.Singleton);

        // Social contextual services registered with Scoped lifetime
        Assert.Contains(services, d => d.ServiceType == typeof(AiPrivacyNoticeGuard) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(AiContextualConsentService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(AiContextualRecommendationService) && d.Lifetime == ServiceLifetime.Scoped);
    }

    #endregion

    #region D. Production Host Non-Activation & Safety Verification

    [Fact]
    public void DefaultHost_DoesNotInvokeSocialContextualAiRegistration()
    {
        var programSource = ReadProgramSource();

        Assert.DoesNotContain("AddSocialContextualAiServices", programSource);
    }

    #endregion

    #region Helpers & Test Doubles

    private static void RegisterAllFourDependencies(IServiceCollection services)
    {
        services.AddScoped<IAiActorAuthorization, StubActorAuthorization>();
        services.AddScoped<IAiPrivacyNoticeGate, StubPrivacyNoticeGate>();
        services.AddScoped<IAiContextualConsentRepository, StubConsentRepository>();
        services.AddScoped<IAiContextualRecommendationGenerator, StubRecommendationGenerator>();
    }

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

    private sealed class StubActorAuthorization : IAiActorAuthorization
    {
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiAuthorizedActor?>(new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module));
    }

    private sealed class StubPrivacyNoticeGate : IAiPrivacyNoticeGate
    {
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(AiPrivacyNoticeGateOutcome.Satisfied);
    }

    private sealed class StubConsentRepository : IAiContextualConsentRepository
    {
        public Task<AiConsentLifecycle?> ReadLifecycleAsync(AiOperationReference operation, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiConsentLifecycle?>(null);

        public Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiConsentResult>(null!);

        public Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiConsentResult>(null!);

        public Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiConsentResult>(null!);

        public Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubRecommendationGenerator : IAiContextualRecommendationGenerator
    {
        public Task<string> GenerateRecommendationAsync(AiModule module, AiAuthorizedActor actor, AiConsentPurpose purpose, CancellationToken cancellationToken = default) =>
            Task.FromResult("Test Recommendation Content");
    }

    #endregion
}
