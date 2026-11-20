using Andril.AI.Interfaces;
using Andril.API.Integrations.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Andril.API.Extensions;

/// <summary>Uninvoked disabled-foundation composition only. No controllers, actor resolver,
/// repositories, generation/mock/provider/transport, maintenance worker or exception handler.
/// Dependencies of these stateless defaults are complete; future authoritative scoped adapters
/// and confirmed identity integration require separate review. This is not an activation API.</summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddDisabledAiFoundation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AiOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiOptions>, AiOptionsValidator>());
        // TryAdd preserves an existing explicitly supplied registration; it does not certify it.
        // Production activation review must inspect the complete service collection independently.
        services.TryAddSingleton<IAiModuleProfileAccessReader, DenyAllAiModuleProfileAccessReader>();
        services.TryAddSingleton<IAiPrivacyNoticeGate, DenyAllAiPrivacyNoticeGate>();
        return services;
    }
}
