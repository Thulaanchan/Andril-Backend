
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Notifications;

/// <summary>
/// Fail-closed notification repository.
///
/// Used only until approved notification persistence,
/// database migrations and M2 authorization are ready.
///
/// Never exposes or writes notification data.
/// Not a production persistence implementation.
/// </summary>
public sealed class DenyAllNotificationRepository
    : INotificationRepository
{
    public Task AddAsync(
        NotificationEntry notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Never silently report a successful write.
        throw new InvalidOperationException(
            "Notification persistence is not enabled.");
    }

    public Task<NotificationEntry?> FindAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<NotificationEntry?>(null);
    }

    public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
        NotificationModuleScope recipientScope,
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<NotificationEntry>>(
            Array.Empty<NotificationEntry>());
    }

    public Task<bool> TryMarkReadAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
