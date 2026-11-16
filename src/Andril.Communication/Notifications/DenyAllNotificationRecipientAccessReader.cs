
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Notifications;

/// <summary>
/// Fail-closed notification recipient access reader.
///
/// Until M2 provides the authoritative private module-profile
/// authorization implementation, no notification recipient
/// scope can be resolved.
///
/// This implementation always denies access and never
/// creates or trusts a synthetic module-profile identity.
/// </summary>
public sealed class DenyAllNotificationRecipientAccessReader
    : INotificationRecipientAccessReader
{
    /// <summary>
    /// Always returns null because private module-profile
    /// ownership and eligibility cannot yet be established.
    ///
    /// Applies to Learning, Hobbies and Social.
    /// </summary>
    public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<NotificationModuleScope?>(null);
    }
}
