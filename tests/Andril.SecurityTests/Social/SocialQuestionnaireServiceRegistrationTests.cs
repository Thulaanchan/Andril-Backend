using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Social;
using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Andril.Social.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Social;

/// <summary>
/// Verifies dependency injection registration security, lifetime integrity,
/// fail-closed behavior, and production host boundaries for Social questionnaire services (SOC-01).
/// </summary>
public sealed class SocialQuestionnaireServiceRegistrationTests
{
    #region A. Default Registration Verification

    [Fact]
    public void AddSocialQuestionnaireServices_RegistersExpectedBaselineDescriptors_WithCorrectLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        var catalogDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(QuestionnaireDefinitionCatalog));
        Assert.NotNull(catalogDescriptor);
        Assert.Equal(ServiceLifetime.Singleton, catalogDescriptor.Lifetime);

        var appServiceDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(QuestionnaireApplicationService));
        Assert.NotNull(appServiceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, appServiceDescriptor.Lifetime);

        var defAccessDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnaireDefinitionAccessService));
        Assert.NotNull(defAccessDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, defAccessDescriptor.Lifetime);
        Assert.Equal(typeof(SocialQuestionnaireDefinitionAccessService), defAccessDescriptor.ImplementationType);

        var valAccessDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnaireSubmissionValidationService));
        Assert.NotNull(valAccessDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, valAccessDescriptor.Lifetime);
        Assert.Equal(typeof(SocialQuestionnaireSubmissionValidationService), valAccessDescriptor.ImplementationType);

        var accessDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(QuestionnaireAccessService));
        Assert.NotNull(accessDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, accessDescriptor.Lifetime);
        Assert.Equal(typeof(QuestionnaireAccessService), accessDescriptor.ImplementationType);
    }

    [Fact]
    public void AddSocialQuestionnaireServices_DoesNotRegisterPersistentResponseServicesOrDbContext()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialQuestionnairePersistentSubmissionOrchestrator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialQuestionnaireResponseScopedLookupService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialQuestionnaireResponseReadAccessService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialQuestionnaireLifecycleAccessService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IQuestionnairePersistenceRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISocialQuestionnaireLifecycleRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SocialDbContext));
    }

    [Fact]
    public void AddSocialQuestionnaireServices_ResolvesCatalogAndApplicationService_WithoutExternalDependencies()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var catalog = scope.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>();
        Assert.NotNull(catalog);

        var definition = catalog.FindByVersion("v1");
        Assert.NotNull(definition);
        Assert.Equal("v1", definition.Version);
        Assert.NotEmpty(definition.Questions);

        var appService = scope.ServiceProvider.GetRequiredService<QuestionnaireApplicationService>();
        Assert.NotNull(appService);

        var appDefinition = appService.GetDefinition("v1");
        Assert.NotNull(appDefinition);
        Assert.Equal("v1", appDefinition.Version);
    }

    [Fact]
    public void AddSocialQuestionnaireServices_ResolvesAllServices_WhenAuthorizationDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<IPrivateModuleAccess, StubModuleAccess>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var catalog = scope.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>();
        var appService = scope.ServiceProvider.GetRequiredService<QuestionnaireApplicationService>();
        var defAccess = scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireDefinitionAccessService>();
        var valService = scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireSubmissionValidationService>();
        var accessService = scope.ServiceProvider.GetRequiredService<QuestionnaireAccessService>();

        Assert.NotNull(catalog);
        Assert.NotNull(appService);
        Assert.NotNull(defAccess);
        Assert.NotNull(valService);
        Assert.NotNull(accessService);
    }

    #endregion

    #region B. Explicit Opt-in Registration Verification

    [Fact]
    public void AddSocialQuestionnaireResponseServices_RegistersExpectedResponseDescriptors_WithScopedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();

        var lookupDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SocialQuestionnaireResponseScopedLookupService));
        Assert.NotNull(lookupDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, lookupDescriptor.Lifetime);

        var orchDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnairePersistentSubmissionOrchestrator));
        Assert.NotNull(orchDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, orchDescriptor.Lifetime);
        Assert.Equal(typeof(SocialQuestionnairePersistentSubmissionOrchestrator), orchDescriptor.ImplementationType);

        var readDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnaireResponseReadAccessService));
        Assert.NotNull(readDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, readDescriptor.Lifetime);
        Assert.Equal(typeof(SocialQuestionnaireResponseReadAccessService), readDescriptor.ImplementationType);

        var lifecycleDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISocialQuestionnaireLifecycleAccessService));
        Assert.NotNull(lifecycleDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, lifecycleDescriptor.Lifetime);
        Assert.Equal(typeof(SocialQuestionnaireLifecycleAccessService), lifecycleDescriptor.ImplementationType);
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_ResolvesAllServices_WhenExplicitDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<IPrivateModuleAccess, StubModuleAccess>();
        services.AddScoped<IQuestionnairePersistenceRepository, StubPersistenceRepository>();
        services.AddScoped<ISocialQuestionnaireLifecycleRepository, StubLifecycleRepository>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var lookup = scope.ServiceProvider.GetRequiredService<SocialQuestionnaireResponseScopedLookupService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISocialQuestionnairePersistentSubmissionOrchestrator>();
        var readAccess = scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireResponseReadAccessService>();
        var lifecycleAccess = scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleAccessService>();

        Assert.NotNull(lookup);
        Assert.NotNull(orchestrator);
        Assert.NotNull(readAccess);
        Assert.NotNull(lifecycleAccess);

        Assert.IsType<SocialQuestionnairePersistentSubmissionOrchestrator>(orchestrator);
        Assert.IsType<SocialQuestionnaireResponseReadAccessService>(readAccess);
        Assert.IsType<SocialQuestionnaireLifecycleAccessService>(lifecycleAccess);
    }

    #endregion

    #region C. Dependency Failure Safety (Fail-Closed)

    [Fact]
    public void AddSocialQuestionnaireServices_MissingProfileService_FailsClosedWhenResolvingDefinitionAccessService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireDefinitionAccessService>());
    }

    [Fact]
    public void AddSocialQuestionnaireServices_MissingProfileService_FailsClosedWhenResolvingSubmissionValidationService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireSubmissionValidationService>());
    }

    [Fact]
    public void AddSocialQuestionnaireServices_MissingModuleAccess_FailsClosedWhenResolvingQuestionnaireAccessService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<QuestionnaireAccessService>());
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_MissingPersistenceRepository_FailsClosedWhenResolvingOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialQuestionnairePersistentSubmissionOrchestrator>());
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_MissingPersistenceRepository_FailsClosedWhenResolvingScopedLookupService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<SocialQuestionnaireResponseScopedLookupService>());
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_MissingPersistenceRepository_FailsClosedWhenResolvingResponseReadAccessService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireResponseReadAccessService>());
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_MissingLifecycleRepository_FailsClosedWhenResolvingLifecycleAccessService()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleAccessService>());
    }

    #endregion

    #region D. Service Lifetime and Registration Integrity

    [Fact]
    public void AddSocialQuestionnaireServices_EnforcesCatalogSingleton_AndServicesScopedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<IPrivateModuleAccess, StubModuleAccess>();

        using var provider = services.BuildServiceProvider();

        QuestionnaireDefinitionCatalog catalogScope1;
        QuestionnaireApplicationService appServiceScope1;
        ISocialQuestionnaireDefinitionAccessService defAccessScope1;
        ISocialQuestionnaireSubmissionValidationService valServiceScope1;
        QuestionnaireAccessService accessServiceScope1;

        using (var scope1 = provider.CreateScope())
        {
            catalogScope1 = scope1.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>();
            appServiceScope1 = scope1.ServiceProvider.GetRequiredService<QuestionnaireApplicationService>();
            defAccessScope1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnaireDefinitionAccessService>();
            valServiceScope1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnaireSubmissionValidationService>();
            accessServiceScope1 = scope1.ServiceProvider.GetRequiredService<QuestionnaireAccessService>();
        }

        using (var scope2 = provider.CreateScope())
        {
            var catalogScope2 = scope2.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>();
            var appServiceScope2 = scope2.ServiceProvider.GetRequiredService<QuestionnaireApplicationService>();
            var defAccessScope2 = scope2.ServiceProvider.GetRequiredService<ISocialQuestionnaireDefinitionAccessService>();
            var valServiceScope2 = scope2.ServiceProvider.GetRequiredService<ISocialQuestionnaireSubmissionValidationService>();
            var accessServiceScope2 = scope2.ServiceProvider.GetRequiredService<QuestionnaireAccessService>();

            // Singleton contract: exact same catalog instance
            Assert.Same(catalogScope1, catalogScope2);

            // Scoped contracts: distinct instances across scopes
            Assert.NotSame(appServiceScope1, appServiceScope2);
            Assert.NotSame(defAccessScope1, defAccessScope2);
            Assert.NotSame(valServiceScope1, valServiceScope2);
            Assert.NotSame(accessServiceScope1, accessServiceScope2);
        }
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_EnforcesResponseServicesScopedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<IPrivateModuleAccess, StubModuleAccess>();
        services.AddScoped<IQuestionnairePersistenceRepository, StubPersistenceRepository>();
        services.AddScoped<ISocialQuestionnaireLifecycleRepository, StubLifecycleRepository>();

        using var provider = services.BuildServiceProvider();

        SocialQuestionnaireResponseScopedLookupService lookup1;
        ISocialQuestionnairePersistentSubmissionOrchestrator orch1;
        ISocialQuestionnaireResponseReadAccessService read1;
        ISocialQuestionnaireLifecycleAccessService lifecycle1;

        using (var scope1 = provider.CreateScope())
        {
            lookup1 = scope1.ServiceProvider.GetRequiredService<SocialQuestionnaireResponseScopedLookupService>();
            orch1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnairePersistentSubmissionOrchestrator>();
            read1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnaireResponseReadAccessService>();
            lifecycle1 = scope1.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleAccessService>();
        }

        using (var scope2 = provider.CreateScope())
        {
            var lookup2 = scope2.ServiceProvider.GetRequiredService<SocialQuestionnaireResponseScopedLookupService>();
            var orch2 = scope2.ServiceProvider.GetRequiredService<ISocialQuestionnairePersistentSubmissionOrchestrator>();
            var read2 = scope2.ServiceProvider.GetRequiredService<ISocialQuestionnaireResponseReadAccessService>();
            var lifecycle2 = scope2.ServiceProvider.GetRequiredService<ISocialQuestionnaireLifecycleAccessService>();

            Assert.NotSame(lookup1, lookup2);
            Assert.NotSame(orch1, orch2);
            Assert.NotSame(read1, read2);
            Assert.NotSame(lifecycle1, lifecycle2);
        }
    }

    [Fact]
    public void AddSocialQuestionnaireServices_RepeatedRegistration_DoesNotDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();
        services.AddSocialQuestionnaireServices();

        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireDefinitionCatalog));
        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireApplicationService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireDefinitionAccessService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireSubmissionValidationService));
        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireAccessService));
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_RepeatedRegistration_DoesNotDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireResponseServices();
        services.AddSocialQuestionnaireResponseServices();

        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireDefinitionCatalog));
        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireApplicationService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireDefinitionAccessService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireSubmissionValidationService));
        Assert.Single(services, d => d.ServiceType == typeof(QuestionnaireAccessService));
        Assert.Single(services, d => d.ServiceType == typeof(SocialQuestionnaireResponseScopedLookupService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnairePersistentSubmissionOrchestrator));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireResponseReadAccessService));
        Assert.Single(services, d => d.ServiceType == typeof(ISocialQuestionnaireLifecycleAccessService));
    }

    [Fact]
    public void AddSocialQuestionnaireServices_PreservesPreExistingCatalogRegistration()
    {
        var customCatalog = new QuestionnaireDefinitionCatalog([QuestionnaireV1Definition.Create()]);
        var services = new ServiceCollection();
        services.AddSingleton(customCatalog);
        services.AddSocialQuestionnaireServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvedCatalog = scope.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>();
        Assert.Same(customCatalog, resolvedCatalog);
    }

    [Fact]
    public void AddSocialQuestionnaireServices_ScopeValidation_SucceedsWithoutCaptiveDependencies()
    {
        var services = new ServiceCollection();
        services.AddSocialQuestionnaireServices();
        services.AddScoped<IPrivateProfileService, StubProfileService>();
        services.AddScoped<IPrivateModuleAccess, StubModuleAccess>();

        // Build with strict scope and dependency validation
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<QuestionnaireDefinitionCatalog>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<QuestionnaireApplicationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireDefinitionAccessService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISocialQuestionnaireSubmissionValidationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<QuestionnaireAccessService>());
    }

    #endregion

    #region E. Production Host Safety Verification

    [Fact]
    public void ProgramSource_CallsAddSocialQuestionnaireServices_AndDoesNotCallDeferredResponsePersistence()
    {
        var programSource = ReadProgramSource();

        const string baselineRegistration = "builder.Services.AddSocialQuestionnaireServices();";
        Assert.Contains(baselineRegistration, programSource);

        var baselineMatches = Regex.Matches(
            programSource,
            @"(?m)^[ \t]*builder\.Services\.AddSocialQuestionnaireServices\(\);[ \t]*\r?$");
        Assert.Single(baselineMatches);

        // Fail-closed host boundary: opt-in response persistence orchestrators must NOT be activated in production host
        Assert.DoesNotContain("AddSocialQuestionnaireResponseServices", programSource);

        // Deferred persistence boundary: Social relational persistence must NOT be activated in production host
        Assert.DoesNotContain("AddSocialPersistence", programSource);
    }

    #endregion

    #region Argument Null Checks

    [Fact]
    public void AddSocialQuestionnaireServices_NullServices_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialQuestionnaireServiceCollectionExtensions.AddSocialQuestionnaireServices(null!));
    }

    [Fact]
    public void AddSocialQuestionnaireResponseServices_NullServices_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialQuestionnaireServiceCollectionExtensions.AddSocialQuestionnaireResponseServices(null!));
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

    private sealed class StubModuleAccess : IPrivateModuleAccess
    {
        public Task RequireAsync(
            string module,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubPersistenceRepository : IQuestionnairePersistenceRepository
    {
        public Task<QuestionnaireResponseEntity?> GetByIdAsync(
            Guid profileId,
            Guid responseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuestionnaireResponseEntity?>(null);

        public Task SaveAsync(
            Guid profileId,
            QuestionnaireResponseEntity response,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubLifecycleRepository : ISocialQuestionnaireLifecycleRepository
    {
        public Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuestionnaireResponseEntity?>(null);

        public Task CreateInitialResponseAsync(
            Guid profileId,
            QuestionnaireResponseEntity initialResponse,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReplaceCurrentResponseAsync(
            Guid profileId,
            Guid existingResponseId,
            QuestionnaireResponseEntity newResponse,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WithdrawResponseAsync(
            Guid profileId,
            Guid responseId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    #endregion
}
