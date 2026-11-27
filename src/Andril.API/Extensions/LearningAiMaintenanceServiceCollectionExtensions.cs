using Andril.AI.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;

namespace Andril.API.Extensions;

/// <summary>
/// Explicit, uninvoked maintenance composition. Requires a separately provisioned
/// maintenance connection and a real worker/retention authority from the caller.
/// Does not migrate, schedule, activate AI routes/providers or certify whole-system purge.
/// </summary>
public static class LearningAiMaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddLearningAiCoreMaintenance(this IServiceCollection services,
        Func<IServiceProvider, DbContextOptions<LearningAiDbContext>> maintenanceOptions,
        Func<IServiceProvider, IAiCoreCleanupAuthority> authority)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(maintenanceOptions);
        ArgumentNullException.ThrowIfNull(authority);
        services.AddScoped(provider => new LearningAiCoreCleanupStore(
            maintenanceOptions(provider) ?? throw new InvalidOperationException("AI maintenance storage is unavailable."),
            authority(provider) ?? throw new InvalidOperationException("AI maintenance authority is unavailable."),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
