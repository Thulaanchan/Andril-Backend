using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Extensions;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.SecurityTests.AI;

public sealed class LearningAiMaintenanceCompositionTests
{
    [Fact]
    public void MaintenanceScopeTelemetryOmitsPrivateIdentityReferences()
    {
        var scope = new AiCoreCleanupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.DoesNotContain(scope.Subject.ToString(), scope.ToString());
        Assert.DoesNotContain(scope.AccountId.ToString(), scope.ToString());
        Assert.DoesNotContain(scope.LearningProfileId.ToString(), scope.ToString());
    }

    private sealed class DeniedAuthority : IAiCoreCleanupAuthority
    {
        public int Calls;
        public Task<T> WithWorkerScopeAsync<T>(Func<AiCoreCleanupScope, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            throw new UnauthorizedAccessException("AI maintenance is not authorized.");
        }
        public Task<bool> MayRemoveCoreAsync(AiConversationCleanupCandidate candidate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unreachable without worker authorization.");
    }

    [Fact]
    public void CompositionDoesNotActivateDatabaseWorkersProvidersOrPrivacyAuthority()
    {
        var services = new ServiceCollection(); var optionsCalls = 0; var authorityCalls = 0;
        services.AddLearningAiCoreMaintenance(_ => { optionsCalls++; return new DbContextOptionsBuilder<LearningAiDbContext>().Options; },
            _ => { authorityCalls++; return new DeniedAuthority(); });
        Assert.Equal(0, optionsCalls); Assert.Equal(0, authorityCalls);
        foreach (var type in new[] { typeof(IHostedService), typeof(IAiProviderDispatchBoundary), typeof(IAiPrivacyNoticeGate),
            typeof(IAiActorAuthorization), typeof(IAiConversationCleanupRepository), typeof(LearningAiDbContext) })
            Assert.DoesNotContain(services, x => x.ServiceType == type);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.Equal(0, optionsCalls); Assert.Equal(0, authorityCalls);
    }

    [Fact]
    public async Task ScopedCompositionRechecksWorkerAuthorityWithoutOpeningDatabase()
    {
        var services = new ServiceCollection(); var authority = new DeniedAuthority();
        services.AddLearningAiCoreMaintenance(_ => new DbContextOptionsBuilder<LearningAiDbContext>().Options, _ => authority);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var store = first.ServiceProvider.GetRequiredService<LearningAiCoreCleanupStore>();
        Assert.Same(store, first.ServiceProvider.GetRequiredService<LearningAiCoreCleanupStore>());
        Assert.NotSame(store, second.ServiceProvider.GetRequiredService<LearningAiCoreCleanupStore>());
        var candidate = new AiConversationCleanupCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(24));
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TryRemoveAsync(candidate));
            Assert.Equal("AI maintenance is not authorized.", error.Message);
        }
        Assert.Equal(2, authority.Calls);
    }

    [Fact]
    public void MissingAuthorityCannotResolveMaintenanceStore()
    {
        var services = new ServiceCollection();
        services.AddLearningAiCoreMaintenance(_ => new DbContextOptionsBuilder<LearningAiDbContext>().Options, _ => null!);
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningAiCoreCleanupStore>());
        Assert.Equal("AI maintenance authority is unavailable.", error.Message);
    }

    [Fact]
    public void MissingStorageCannotResolveMaintenanceStore()
    {
        var services = new ServiceCollection(); services.AddLearningAiCoreMaintenance(_ => null!, _ => new DeniedAuthority());
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<LearningAiCoreCleanupStore>());
        Assert.Equal("AI maintenance storage is unavailable.", error.Message);
    }
}
