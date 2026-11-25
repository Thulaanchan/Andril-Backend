using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Controllers.Groups;
using Andril.API.Extensions;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Andril.Persistence.DbContexts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Groups;

/// <summary>
/// Verifies dependency injection registration security, lifetime integrity,
/// strict constructor enforcement, fail-closed fallback prevention, and production host boundaries
/// for Group membership coordination services (CF-07).
/// </summary>
public sealed class GroupMembershipServiceRegistrationTests
{
    #region A. Descriptor & Lifetime Verification

    [Fact]
    public void AddGroupMembershipServices_RegistersExpectedDescriptors_WithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();

        var interfaceDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IGroupMembershipOperationService));
        Assert.NotNull(interfaceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, interfaceDescriptor.Lifetime);

        var concreteDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(GroupMembershipOperationService));
        Assert.NotNull(concreteDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, concreteDescriptor.Lifetime);
    }

    [Fact]
    public void AddGroupMembershipServices_CalledRepeatedly_DoesNotIntroduceDuplicateDescriptors()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        services.AddGroupMembershipServices();
        services.AddGroupMembershipServices();

        var interfaceDescriptors = services.Where(d => d.ServiceType == typeof(IGroupMembershipOperationService)).ToList();
        Assert.Single(interfaceDescriptors);

        var concreteDescriptors = services.Where(d => d.ServiceType == typeof(GroupMembershipOperationService)).ToList();
        Assert.Single(concreteDescriptors);
    }

    [Fact]
    public void AddGroupMembershipServices_PreservesPreExistingRegistrations()
    {
        var services = new ServiceCollection();
        var customMock = new StubCustomGroupMembershipOperationService();
        services.AddScoped<IGroupMembershipOperationService>(_ => customMock);

        services.AddGroupMembershipServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();
        Assert.Same(customMock, resolved);
    }

    [Fact]
    public void AddGroupMembershipServices_DoesNotRegisterFakeProductionRepositoriesOrDbContext()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupEntryRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupMembershipRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupJoinRequestRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupMembershipTransactionRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupAuthoritativeEvidenceProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGroupActorContext));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(GroupMembershipController));
    }

    #endregion

    #region B. Fail-Closed Dependency Resolution & Strict Fallback Prevention

    [Fact]
    public void AddGroupMembershipServices_ResolvesSuccessfully_WhenAllSixStrictDependenciesSupplied()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();
        Assert.NotNull(service);
        Assert.IsType<GroupMembershipOperationService>(service);

        var concrete = scope.ServiceProvider.GetRequiredService<GroupMembershipOperationService>();
        Assert.NotNull(concrete);
        Assert.Same(service, concrete);
    }

    [Fact]
    public void AddGroupMembershipServices_SeparateRequestScopes_ReceiveSeparateInstances()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var instance1 = scope1.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();
        var instance2 = scope2.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();

        Assert.NotNull(instance1);
        Assert.NotNull(instance2);
        Assert.NotSame(instance1, instance2);

        var concrete1 = scope1.ServiceProvider.GetRequiredService<GroupMembershipOperationService>();
        var concrete2 = scope2.ServiceProvider.GetRequiredService<GroupMembershipOperationService>();

        Assert.Same(instance1, concrete1);
        Assert.Same(instance2, concrete2);
    }

    [Fact]
    public void AddGroupMembershipServices_IncompatibleCustomInterface_FailsSafelyWithInvalidOperationException()
    {
        var services = new ServiceCollection();
        var customMock = new StubCustomGroupMembershipOperationService();
        services.AddScoped<IGroupMembershipOperationService>(_ => customMock);
        services.AddGroupMembershipServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvedInterface = scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();
        Assert.Same(customMock, resolvedInterface);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<GroupMembershipOperationService>());

        Assert.Contains("is not compatible with concrete GroupMembershipOperationService resolution", ex.Message);
    }

    [Fact]
    public void AddGroupMembershipServices_IncompatibleCustomInterface_DoesNotBypassOrConstructIndependentService()
    {
        var services = new ServiceCollection();
        var customMock = new StubCustomGroupMembershipOperationService();
        services.AddScoped<IGroupMembershipOperationService>(_ => customMock);
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<GroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingEntryRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupEntryRepository)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingMembershipRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupMembershipRepository)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingJoinRequestRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupJoinRequestRepository)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingTransactionRepository_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupMembershipTransactionRepository)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingEvidenceProvider_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupAuthoritativeEvidenceProvider)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_MissingActorContext_FailsClosedWhenResolving()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);
        services.Remove(services.First(d => d.ServiceType == typeof(IGroupActorContext)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    [Fact]
    public void AddGroupMembershipServices_OnlyLegacyThreeDependenciesRegistered_FailsClosedAndDoesNotFallBackToLegacyConstructor()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();

        // Register ONLY the legacy 3 dependencies
        services.AddScoped<IGroupEntryRepository, InMemoryGroupEntryRepository>();
        services.AddScoped<IGroupMembershipRepository, InMemoryGroupMembershipRepository>();
        services.AddScoped<IGroupJoinRequestRepository, InMemoryGroupJoinRequestRepository>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Must fail closed because transaction repository, evidence provider, and actor context are absent
        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>());
    }

    #endregion

    #region C. Container Validation & Scope Integrity

    [Fact]
    public void AddGroupMembershipServices_StrictContainerValidation_PassesWithSuppliedDependencies()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IGroupMembershipOperationService>();
        Assert.NotNull(resolved);
    }

    [Fact]
    public void AddGroupMembershipServices_CannotResolveFromRootProvider_WhenScoped()
    {
        var services = new ServiceCollection();
        services.AddGroupMembershipServices();
        RegisterAllSixStrictDependencies(services);

        var options = new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        };

        using var provider = services.BuildServiceProvider(options);

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IGroupMembershipOperationService>());
    }

    #endregion

    #region D. Production Host Non-Activation & Controller Discovery Boundaries

    [Fact]
    public void DefaultHost_DoesNotInvokeGroupMembershipRegistration()
    {
        var programSource = ReadProgramSource();

        Assert.DoesNotContain("AddGroupMembershipServices", programSource);
    }

    [Fact]
    public void GroupMembershipController_IsDecoratedWithNonControllerAttribute()
    {
        var attribute = typeof(GroupMembershipController).GetCustomAttribute<NonControllerAttribute>(inherit: false);
        Assert.NotNull(attribute);
    }

    #endregion

    #region Helpers & Test Doubles

    private static void RegisterAllSixStrictDependencies(IServiceCollection services)
    {
        services.AddScoped<IGroupEntryRepository, InMemoryGroupEntryRepository>();
        services.AddScoped<IGroupMembershipRepository, InMemoryGroupMembershipRepository>();
        services.AddScoped<IGroupJoinRequestRepository, InMemoryGroupJoinRequestRepository>();
        services.AddScoped<IGroupMembershipTransactionRepository, StubGroupMembershipTransactionRepository>();
        services.AddScoped<IGroupAuthoritativeEvidenceProvider, StubGroupAuthoritativeEvidenceProvider>();
        services.AddScoped<IGroupActorContext, StubGroupActorContext>();
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

    private sealed class InMemoryGroupEntryRepository : IGroupEntryRepository
    {
        public Task<GroupEntry?> GetGroupAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<GroupEntry?>(null);

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class InMemoryGroupMembershipRepository : IGroupMembershipRepository
    {
        public Task<GroupMembership?> GetMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GroupMembership?>(null);

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RemoveMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class InMemoryGroupJoinRequestRepository : IGroupJoinRequestRepository
    {
        public Task<GroupJoinRequest?> GetJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GroupJoinRequest?>(null);

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task SaveJoinRequestAsync(GroupJoinRequest joinRequest, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RemoveJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> GetJoinRequestCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class StubGroupMembershipTransactionRepository : IGroupMembershipTransactionRepository
    {
        public Task<GroupJoinRequestSubmissionTransactionResult> SubmitJoinRequestAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid requestingProfileId, DateTime requestedAtUtc,
            long? expectedRevision = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GroupJoinRequestSubmissionTransactionResult(true, GroupMembershipTransactionStatus.Committed));

        public Task<GroupJoinRequestCancellationTransactionResult> CancelJoinRequestAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid requestingProfileId,
            long? expectedRevision = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GroupJoinRequestCancellationTransactionResult(true, GroupMembershipTransactionStatus.Committed, requestCancelled: true));

        public Task<GroupAdmissionTransactionResult> ProcessAdmissionAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid applicantProfileId, bool approve,
            Guid actorProfileId, DateTime decisionAtUtc, long? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GroupAdmissionTransactionResult(true, GroupMembershipTransactionStatus.Committed, membershipCreated: true, requestResolved: true));

        public Task<GroupDepartureTransactionResult> LeaveGroupAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid memberProfileId,
            long? expectedRevision = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GroupDepartureTransactionResult(true, GroupMembershipTransactionStatus.Committed, membershipRemoved: true));

        public Task<GroupTransactionReconciliationResult> ReconcileTransactionOutcomeAsync(
            Guid groupId, GroupModuleScope scope, Guid operationCorrelationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupTransactionReconciliationResult.Confirmed(GroupMembershipTransactionStatus.Committed));
    }

    private sealed class StubGroupAuthoritativeEvidenceProvider : IGroupAuthoritativeEvidenceProvider
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(
            Guid profileId, string module, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthoritativeGroupProfileEvidence?>(null);

        public Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthoritativeGroupStateEvidence?>(null);

        public Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(
            Guid groupId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthoritativeGroupMembershipEvidence?>(null);

        public Task<bool> IsGroupOwnerAsync(
            Guid groupId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetCurrentMemberCountAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int?> GetGroupCapacityAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);
    }

    private sealed class StubGroupActorContext : IGroupActorContext
    {
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(
            string expectedModule, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthoritativeGroupProfileEvidence?>(null);
    }

    private sealed class StubCustomGroupMembershipOperationService : IGroupMembershipOperationService
    {
        public Task<GroupJoinOperationResult> SubmitJoinRequestAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, int callerAge = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.RequestSubmitted());

        public Task<GroupJoinOperationResult> CancelJoinRequestAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.RequestCancelled());

        public Task<GroupJoinOperationResult> ProcessJoinRequestAsync(Guid groupId, GroupModuleScope scope, string actorModule, Guid actorProfileId, bool isActorAuthorizedRole, Guid applicantProfileId, bool approve, int applicantAge = 25, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.Approved());

        public Task<GroupJoinOperationResult> LeaveGroupAsync(Guid groupId, GroupModuleScope scope, string callerModule, Guid callerProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.Left());

        public Task<GroupJoinOperationResult> SubmitJoinRequestStrictAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.RequestSubmitted());

        public Task<GroupJoinOperationResult> CancelJoinRequestStrictAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.RequestCancelled());

        public Task<GroupJoinOperationResult> ProcessJoinRequestStrictAsync(Guid groupId, GroupModuleScope scope, Guid applicantProfileId, bool approve, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.Approved());

        public Task<GroupJoinOperationResult> LeaveGroupStrictAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(GroupJoinOperationResult.Left());
    }

    #endregion
}
