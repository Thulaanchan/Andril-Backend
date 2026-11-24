using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.DTOs;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationModuleAccessPropagationTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task AuthorizedList_PassesTrustedAccountAndModuleAndUsesResolvedScope(
        NotificationModule module)
    {
        var accountId = Guid.NewGuid();
        var scope = new NotificationModuleScope(
            module,
            Guid.NewGuid());
        var entry = new NotificationEntry(
            Guid.NewGuid(),
            scope,
            "message",
            "New Message",
            null,
            CreatedAtUtc);
        var access = new RecordingAccessReader(scope);
        var repository = new RecordingRepository(entry);
        var service = new NotificationApplicationService(
            access,
            repository);

        var result = await service.ListLatestAsync(
            accountId,
            module,
            10);

        Assert.Equal(accountId, access.LastAccountId);
        Assert.Equal(module, access.LastModule);
        Assert.Equal(scope, repository.LastScope);
        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal(entry.Id, item.Id);
                Assert.Equal(entry.Title, item.Title);
            });
    }

    private sealed class RecordingAccessReader(
        NotificationModuleScope? scope)
        : INotificationRecipientAccessReader
    {
        public Guid LastAccountId { get; private set; }

        public NotificationModule LastModule { get; private set; }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid authenticatedAccountId,
            NotificationModule module,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAccountId = authenticatedAccountId;
            LastModule = module;
            return Task.FromResult(scope);
        }
    }

    private sealed class RecordingRepository(
        NotificationEntry entry)
        : INotificationRepository
    {
        public NotificationModuleScope? LastScope { get; private set; }

        public Task AddAsync(
            NotificationEntry notification,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<NotificationEntry>> ListLatestAsync(
            NotificationModuleScope recipientScope,
            int maxItems,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScope = recipientScope;
            IReadOnlyList<NotificationEntry> entries = new[] { entry };
            return Task.FromResult(entries);
        }

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
