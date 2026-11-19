
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.DTOs;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;

namespace Andril.Communication.Services;

/// <summary>
/// Application service for module-scoped notification reads.
///
/// The account identifier must come from trusted server-side
/// authentication, never directly from a client request.
///
/// All repository operations require an authoritative,
/// active private module profile.
/// </summary>
public sealed class NotificationApplicationService
{
    // Proposed internal safety bound, not a Free/Premium quota.
    private const int MaxInternalPageSize = 100;

    private readonly INotificationRecipientAccessReader _accessReader;
    private readonly INotificationRepository _repository;

    public NotificationApplicationService(
        INotificationRecipientAccessReader accessReader,
        INotificationRepository repository)
    {
        _accessReader = accessReader
            ?? throw new ArgumentNullException(nameof(accessReader));

        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Returns notifications only for an authorized private
    /// module profile. Unauthorized requests return an empty list.
    /// </summary>
    public async Task<IReadOnlyList<NotificationListItemDto>>
        ListLatestAsync(
            Guid authenticatedAccountId,
            NotificationModule module,
            int maxItems,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxItems),
                "The requested item count must be positive.");
        }

        var scope = await ResolveScopeAsync(
            authenticatedAccountId,
            module,
            cancellationToken);

        if (scope is null)
        {
            return Array.Empty<NotificationListItemDto>();
        }

        var entries = await _repository.ListLatestAsync(
            scope,
            Math.Min(maxItems, MaxInternalPageSize),
            cancellationToken);

        if (entries is null)
        {
            return Array.Empty<NotificationListItemDto>();
        }

        // Fail closed if the repository returns even one
        // notification outside the authorized module scope.
        foreach (var entry in entries)
        {
            if (entry is null || entry.RecipientScope != scope)
            {
                return Array.Empty<NotificationListItemDto>();
            }
        }

        var results = new List<NotificationListItemDto>(
            entries.Count);

        foreach (var entry in entries)
        {
            results.Add(ToDto(entry));
        }

        return results;
    }

    /// <summary>
    /// Finds one notification without exposing another
    /// recipient's notification or its existence.
    /// </summary>
    public async Task<NotificationListItemDto?> FindAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (notificationId == Guid.Empty)
        {
            return null;
        }

        var scope = await ResolveScopeAsync(
            authenticatedAccountId,
            module,
            cancellationToken);

        if (scope is null)
        {
            return null;
        }

        var entry = await _repository.FindAsync(
            scope,
            notificationId,
            cancellationToken);

        if (entry is null ||
            entry.Id != notificationId ||
            entry.RecipientScope != scope)
        {
            return null;
        }

        return ToDto(entry);
    }

    /// <summary>
    /// Marks one authorized notification as read.
    ///
    /// Returns false for denied, missing, or invalid targets.
    /// The repository must enforce recipient scope atomically.
    /// </summary>
    public async Task<bool> TryMarkReadAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        Guid notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (notificationId == Guid.Empty)
        {
            return false;
        }

        var scope = await ResolveScopeAsync(
            authenticatedAccountId,
            module,
            cancellationToken);

        if (scope is null)
        {
            return false;
        }

        return await _repository.TryMarkReadAsync(
            scope,
            notificationId,
            readAtUtc.ToUniversalTime(),
            cancellationToken);
    }

    private async Task<NotificationModuleScope?> ResolveScopeAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        CancellationToken cancellationToken)
    {
        if (authenticatedAccountId == Guid.Empty ||
            !Enum.IsDefined(module))
        {
            return null;
        }

        var scope = await _accessReader
            .ResolveOwnedActiveScopeAsync(
                authenticatedAccountId,
                module,
                cancellationToken);

        if (scope is null ||
            scope.Module != module ||
            scope.RecipientModuleProfileId == Guid.Empty)
        {
            return null;
        }

        return scope;
    }

    private static NotificationListItemDto ToDto(
        NotificationEntry entry)
    {
        return new NotificationListItemDto(
            entry.Id,
            entry.Type,
            entry.Title,
            entry.Preview,
            entry.CreatedAtUtc,
            entry.ReadAtUtc);
    }
}
