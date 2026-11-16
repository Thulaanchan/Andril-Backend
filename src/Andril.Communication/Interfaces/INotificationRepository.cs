
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Internal persistence contract for module-scoped notifications.
///
/// This interface does not authenticate callers or prove
/// ownership of a module profile. The application service
/// must validate M2's authoritative identity and permissions
/// before invoking any repository method.
/// </summary>
public interface INotificationRepository
{
    Task AddAsync(
        NotificationEntry notification,
        CancellationToken cancellationToken = default);

    Task<NotificationEntry?> FindAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
        NotificationModuleScope recipientScope,
        int maxItems,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an owned notification as read.
    /// Repeated reads must preserve the original read timestamp.
    /// Returns false when no notification matches the scope and ID.
    /// </summary>
    Task<bool> TryMarkReadAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken = default);
}
