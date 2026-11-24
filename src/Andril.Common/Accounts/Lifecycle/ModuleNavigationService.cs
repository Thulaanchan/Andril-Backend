using System.Data.Common;
using Andril.Common.Accounts.Policies;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Lifecycle;

internal enum ModuleNavigationState { Active, Inactive, NeverActivated, Denied, Unavailable }

// This result is navigation data. It is never accepted as authorization by a protected operation.
internal sealed record ModuleNavigationStatus(ModuleNavigationState State, string? Module = null, long? Revision = null);
internal sealed record ModuleNavigationSnapshot(ModuleActivationEvidence Evidence, ModuleMembership? Membership);

internal interface IModuleNavigationReader
{
    Task<ModuleNavigationSnapshot> ReadNavigationAsync(Guid subject, string module, CancellationToken cancellationToken);
}

internal sealed class ModuleNavigationService(ICurrentUser user, IModuleNavigationReader reader)
{
    public async Task<ModuleNavigationStatus> ReadAsync(string module, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ModuleNames.IsValid(module) || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            return new(ModuleNavigationState.Denied);

        try
        {
            var snapshot = await reader.ReadNavigationAsync(subject, module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!user.IsAuthenticated || user.UserId != subject)
                return new(ModuleNavigationState.Denied);

            var evidence = snapshot.Evidence;
            var membership = snapshot.Membership;
            if (evidence.ConfirmedSubject != subject && evidence.SourceOutcome == ModuleAuthorizationOutcome.Satisfied)
                return new(ModuleNavigationState.Denied);

            ActiveModuleMembership.ValidateEvidence(new(evidence.Scope.AccountId, module), evidence, membership);
            if (membership is { Revision: <= 0 })
                return new(ModuleNavigationState.Unavailable);

            return membership switch
            {
                null => new(ModuleNavigationState.NeverActivated, module),
                ActiveModuleMembership active => new(ModuleNavigationState.Active, module, active.Revision),
                InactiveModuleMembership inactive => new(ModuleNavigationState.Inactive, module, inactive.Revision),
                _ => new(ModuleNavigationState.Unavailable)
            };
        }
        catch (ModuleLifecycleException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(error.Outcome == ModuleAuthorizationOutcome.Unavailable
                ? ModuleNavigationState.Unavailable : ModuleNavigationState.Denied);
        }
        catch (Exception error) when (error is AccountStoreUnavailableException or PolicyConfigurationException
            or DbException or TimeoutException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(ModuleNavigationState.Unavailable);
        }
    }
}
