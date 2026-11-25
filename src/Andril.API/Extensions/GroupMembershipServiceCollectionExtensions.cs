using System;
using Andril.Groups.Interfaces;
using Andril.Groups.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Service collection extensions for configuring strict opt-in Group membership transaction coordination services (CF-07).
/// Registers <see cref="IGroupMembershipOperationService"/> and <see cref="GroupMembershipOperationService"/>
/// strictly requiring all authoritative and atomic transaction dependencies without falling back to legacy constructors.
/// </summary>
public static class GroupMembershipServiceCollectionExtensions
{
    /// <summary>
    /// Registers strict opt-in Group membership coordination services with Scoped lifetime.
    /// Strictly requires all six dependencies:
    /// <see cref="IGroupEntryRepository"/>, <see cref="IGroupMembershipRepository"/>,
    /// <see cref="IGroupJoinRequestRepository"/>, <see cref="IGroupMembershipTransactionRepository"/>,
    /// <see cref="IGroupAuthoritativeEvidenceProvider"/>, and <see cref="IGroupActorContext"/>.
    /// Explicitly prevents silent fallback to the obsolete legacy three-dependency constructor.
    /// Fails closed if any dependency is missing upon resolution.
    /// Does not activate Group endpoints, controllers, or database persistence.
    /// </summary>
    public static IServiceCollection AddGroupMembershipServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IGroupMembershipOperationService>(sp =>
        {
            var entryRepository = sp.GetRequiredService<IGroupEntryRepository>();
            var membershipRepository = sp.GetRequiredService<IGroupMembershipRepository>();
            var joinRequestRepository = sp.GetRequiredService<IGroupJoinRequestRepository>();
            var transactionRepository = sp.GetRequiredService<IGroupMembershipTransactionRepository>();
            var evidenceProvider = sp.GetRequiredService<IGroupAuthoritativeEvidenceProvider>();
            var actorContext = sp.GetRequiredService<IGroupActorContext>();

            return new GroupMembershipOperationService(
                entryRepository,
                membershipRepository,
                joinRequestRepository,
                transactionRepository,
                evidenceProvider,
                actorContext);
        });

        services.TryAddScoped<GroupMembershipOperationService>(sp =>
            (GroupMembershipOperationService)sp.GetRequiredService<IGroupMembershipOperationService>());

        return services;
    }
}
