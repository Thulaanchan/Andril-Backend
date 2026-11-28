using Andril.API.Integrations.Learning;
using Andril.Learning.Interfaces;
using Andril.Learning.Activities;
using Andril.Learning.Progress;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>Opt-in shared owner read boundary only. Requires existing real M2 services; installs no purpose grants, repository, endpoint or provider.</summary>
public static class LearningOwnerAuthorityServiceCollectionExtensions
{
    public static IServiceCollection AddLearningOwnerAuthority(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICurrentLearningOwnerBoundary,CurrentLearningOwnerBoundary>();
        services.TryAddScoped<ILearningActivityAuthority>(provider=>new CurrentLearningOwnerActivityAuthority(provider.GetRequiredService<ICurrentLearningOwnerBoundary>()));
        services.TryAddScoped<ILearningXpAuthority,CurrentLearningXpReadAuthority>();
        return services;
    }
}
