
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// Test-only, in-memory notification repository.
///
/// Provides module-profile scoped storage for unit tests.
/// Does not authenticate users or provide production persistence.
///
/// Never register this implementation in production DI.
/// </summary>
public sealed class InMemoryNotificationRepository
    : INotificationRepository
{
    private readonly object _gate = new();

    private readonly Dictionary<Guid, NotificationEntry>
        _notifications = new();

    public Task AddAsync(
        NotificationEntry notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notification);

        lock (_gate)
        {
            if (_notifications.ContainsKey(notification.Id))
            {
                throw new InvalidOperationException(
                    "Duplicate notification identifier.");
            }

            _notifications.Add(notification.Id, notification);
        }

        return Task.CompletedTask;
    }

    public Task<NotificationEntry?> FindAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recipientScope);

        lock (_gate)
        {
            if (_notifications.TryGetValue(
                    notificationId,
                    out var notification)
                && notification.RecipientScope == recipientScope)
            {
                return Task.FromResult<NotificationEntry?>(
                    notification);
            }
        }

        return Task.FromResult<NotificationEntry?>(null);
    }

    public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
        NotificationModuleScope recipientScope,
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recipientScope);

        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxItems));
        }

        lock (_gate)
        {
            IReadOnlyList<NotificationEntry> results =
                _notifications.Values
                    .Where(notification =>
                        notification.RecipientScope == recipientScope)
                    .OrderByDescending(notification =>
                        notification.CreatedAtUtc)
                    .ThenByDescending(notification =>
                        notification.Id)
                    .Take(maxItems)
                    .ToArray();

            return Task.FromResult(results);
        }
    }

    public Task<bool> TryMarkReadAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recipientScope);

        lock (_gate)
        {
            if (!_notifications.TryGetValue(
                    notificationId,
                    out var notification))
            {
                return Task.FromResult(false);
            }

            if (notification.RecipientScope != recipientScope)
            {
                return Task.FromResult(false);
            }

            notification.MarkAsRead(readAtUtc);

            return Task.FromResult(true);
        }
    }
}
