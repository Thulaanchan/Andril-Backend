
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Proposed EF Core repository for private, module-scoped
/// notifications.
///
/// NOT READY FOR PRODUCTION REGISTRATION.
///
/// Requires an approved notification DbContext, database
/// mapping, migration, and M2 authorization integration.
///
/// Never register this repository against an unconfigured
/// DbContext or expose it through an HTTP endpoint.
/// </summary>
public sealed class PostgresNotificationRepository
    : INotificationRepository
{
    private readonly DbContext _db;

    public PostgresNotificationRepository(DbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task AddAsync(
        NotificationEntry notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notification);

        // Caller must have already verified the recipient
        // using authoritative M2 module-profile authorization.
        var record = NotificationPersistenceRecord.Create(
            notification.Id,
            notification.RecipientScope.RecipientModuleProfileId,
            ToStoredModule(notification.RecipientScope.Module),
            notification.Type,
            notification.Title,
            notification.Preview,
            notification.CreatedAtUtc);

        if (notification.ReadAtUtc is { } readAt)
        {
            record.MarkAsRead(readAt);
        }

        await _db.Set<NotificationPersistenceRecord>()
            .AddAsync(record, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NotificationEntry?> FindAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recipientScope);

        if (notificationId == Guid.Empty)
        {
            return null;
        }

        var record = await Scoped(recipientScope)
            .SingleOrDefaultAsync(
                item => item.Id == notificationId,
                cancellationToken);

        return record is null ? null : ToDomain(record);
    }

    public async Task<IReadOnlyList<NotificationEntry>>
        ListLatestAsync(
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

        var records = await Scoped(recipientScope)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(maxItems)
            .ToListAsync(cancellationToken);

        return records
            .Select(ToDomain)
            .ToArray();
    }

    public async Task<bool> TryMarkReadAsync(
        NotificationModuleScope recipientScope,
        Guid notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recipientScope);

        if (notificationId == Guid.Empty)
        {
            return false;
        }

        if (readAtUtc == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAtUtc));
        }

        var utc = readAtUtc.ToUniversalTime();

        // This read is restricted to the exact recipient
        // module and private module-profile identifier.
        var existing = await Scoped(recipientScope)
            .Where(item => item.Id == notificationId)
            .Select(item => new
            {
                item.CreatedAtUtc,
                item.ReadAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return false;
        }

        if (existing.ReadAtUtc.HasValue)
        {
            // Preserve the original timestamp.
            return true;
        }

        if (utc < existing.CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAtUtc));
        }

        // Conditional database update:
        // only the first successful reader sets ReadAtUtc.
        var affected = await Scoped(recipientScope)
            .Where(item =>
                item.Id == notificationId &&
                item.ReadAtUtc == null &&
                item.CreatedAtUtc <= utc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    item => item.ReadAtUtc,
                    (DateTimeOffset?)utc),
                cancellationToken);

        if (affected > 0)
        {
            return true;
        }

        // Another request may have marked it as read.
        // Check the same authorized scope again.
        return await Scoped(recipientScope)
            .AnyAsync(
                item =>
                    item.Id == notificationId &&
                    item.ReadAtUtc != null,
                cancellationToken);
    }

    /// <summary>
    /// Every read and update is restricted to both
    /// module and private recipient module-profile ID.
    /// </summary>
    private IQueryable<NotificationPersistenceRecord> Scoped(
        NotificationModuleScope recipientScope)
    {
        var module = ToStoredModule(recipientScope.Module);
        var profileId = recipientScope.RecipientModuleProfileId;

        return _db.Set<NotificationPersistenceRecord>()
            .AsNoTracking()
            .Where(item =>
                item.RecipientModuleProfileId == profileId &&
                item.Module == module);
    }

    /// <summary>
    /// Current repository-compatible module mapping.
    /// The pending Social/Dating naming decision must
    /// be resolved before production activation.
    /// </summary>
    private static string ToStoredModule(
        NotificationModule module)
    {
        return module switch
        {
            NotificationModule.Learning => "learning",
            NotificationModule.Hobbies => "hobbies",
            NotificationModule.Social => "social",
            _ => throw new ArgumentOutOfRangeException(
                nameof(module))
        };
    }

    private static NotificationModule FromStoredModule(
        string module)
    {
        return module switch
        {
            "learning" => NotificationModule.Learning,
            "hobbies" => NotificationModule.Hobbies,
            "social" => NotificationModule.Social,
            _ => throw new InvalidOperationException(
                "Unsupported stored notification module.")
        };
    }

    private static NotificationEntry ToDomain(
        NotificationPersistenceRecord record)
    {
        var scope = new NotificationModuleScope(
            FromStoredModule(record.Module),
            record.RecipientModuleProfileId);

        var notification = new NotificationEntry(
            record.Id,
            scope,
            record.Type,
            record.Title,
            record.Preview,
            record.CreatedAtUtc);

        if (record.ReadAtUtc.HasValue)
        {
            notification.MarkAsRead(record.ReadAtUtc.Value);
        }

        return notification;
    }
}
