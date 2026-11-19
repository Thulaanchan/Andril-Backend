
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationApplicationServiceTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static NotificationModuleScope CreateScope(
        NotificationModule module = NotificationModule.Social)
    {
        return new NotificationModuleScope(
            module,
            Guid.NewGuid());
    }

    private static NotificationEntry CreateEntry(
        NotificationModuleScope scope,
        Guid? id = null,
        DateTimeOffset? readAt = null)
    {
        var entry = new NotificationEntry(
            id ?? Guid.NewGuid(),
            scope,
            "message",
            "New Message",
            "You received a message.",
            CreatedAt);

        if (readAt.HasValue)
        {
            entry.MarkAsRead(readAt.Value);
        }

        return entry;
    }

    private static NotificationApplicationService CreateService(
        FakeAccessReader access,
        FakeRepository repository)
    {
        return new NotificationApplicationService(
            access,
            repository);
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task UnauthorizedScope_ShouldDenyAllOperations(
        NotificationModule module)
    {
        var access = new FakeAccessReader();
        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var list = await service.ListLatestAsync(
            accountId, module, 10);

        var found = await service.FindAsync(
            accountId, module, notificationId);

        var marked = await service.TryMarkReadAsync(
            accountId,
            module,
            notificationId,
            CreatedAt.AddMinutes(10));

        Assert.Empty(list);
        Assert.Null(found);
        Assert.False(marked);

        Assert.Equal(3, access.Calls);
        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.MarkReadCalls);
    }

    [Fact]
    public async Task EmptyAccountId_ShouldDenyWithoutRepositoryAccess()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        var list = await service.ListLatestAsync(
            Guid.Empty,
            NotificationModule.Social,
            10);

        Assert.Empty(list);
        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.ListCalls);
    }

    [Fact]
    public async Task InvalidModule_ShouldDenyWithoutRepositoryAccess()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        var list = await service.ListLatestAsync(
            Guid.NewGuid(),
            (NotificationModule)999,
            10);

        Assert.Empty(list);
        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.ListCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidPageSize_ShouldBeRejected(
        int maxItems)
    {
        var access = new FakeAccessReader();
        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ListLatestAsync(
                Guid.NewGuid(),
                NotificationModule.Social,
                maxItems));

        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.ListCalls);
    }

    [Fact]
    public async Task AuthorizedList_ShouldReturnDtosAndCapPageSize()
    {
        var scope = CreateScope();
        var access = new FakeAccessReader
        {
            Scope = scope
        };

        var first = CreateEntry(scope);
        var second = CreateEntry(
            scope,
            readAt: CreatedAt.AddMinutes(5));

        var repository = new FakeRepository
        {
            ListResult = new[] { first, second }
        };

        var service = CreateService(access, repository);

        var results = await service.ListLatestAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            1000);

        Assert.Equal(2, results.Count);
        Assert.Equal(100, repository.LastMaxItems);
        Assert.Equal(scope, repository.LastScope);

        Assert.Equal(first.Id, results[0].Id);
        Assert.Equal(first.Title, results[0].Title);
        Assert.Equal(first.Preview, results[0].Preview);
        Assert.False(results[0].IsRead);

        Assert.Equal(second.Id, results[1].Id);
        Assert.True(results[1].IsRead);
        Assert.Equal(
            second.ReadAtUtc,
            results[1].ReadAtUtc);
    }

    [Fact]
    public async Task ForeignNotificationInList_ShouldDenyEntirePage()
    {
        var ownedScope = CreateScope(
            NotificationModule.Social);

        var foreignScope = CreateScope(
            NotificationModule.Hobbies);

        var access = new FakeAccessReader
        {
            Scope = ownedScope
        };

        var repository = new FakeRepository
        {
            ListResult = new[]
            {
                CreateEntry(ownedScope),
                CreateEntry(foreignScope)
            }
        };

        var service = CreateService(access, repository);

        var results = await service.ListLatestAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            10);

        Assert.Empty(results);
        Assert.Equal(1, repository.ListCalls);
    }

    [Fact]
    public async Task NullEntryInList_ShouldDenyEntirePage()
    {
        var scope = CreateScope();

        var access = new FakeAccessReader
        {
            Scope = scope
        };

        var repository = new FakeRepository
        {
            ListResult = new NotificationEntry[]
            {
                CreateEntry(scope),
                null!
            }
        };

        var service = CreateService(access, repository);

        var results = await service.ListLatestAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task NullRepositoryList_ShouldReturnEmpty()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };

        var repository = new FakeRepository
        {
            ListResult = null!
        };

        var service = CreateService(access, repository);

        var results = await service.ListLatestAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindAsync_ShouldReturnOwnedNotification()
    {
        var scope = CreateScope();
        var entry = CreateEntry(scope);

        var access = new FakeAccessReader
        {
            Scope = scope
        };

        var repository = new FakeRepository
        {
            FindResult = entry
        };

        var service = CreateService(access, repository);

        var result = await service.FindAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            entry.Id);

        Assert.NotNull(result);
        Assert.Equal(entry.Id, result.Id);
        Assert.Equal(entry.Title, result.Title);
        Assert.Equal(entry.CreatedAtUtc, result.CreatedAtUtc);
        Assert.Equal(scope, repository.LastScope);
    }

    [Fact]
    public async Task FindAsync_ShouldDenyForeignProfileNotification()
    {
        var ownedScope = CreateScope(
            NotificationModule.Social);

        var foreignScope = CreateScope(
            NotificationModule.Social);

        var entry = CreateEntry(foreignScope);

        var access = new FakeAccessReader
        {
            Scope = ownedScope
        };

        var repository = new FakeRepository
        {
            FindResult = entry
        };

        var service = CreateService(access, repository);

        var result = await service.FindAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            entry.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_ShouldRejectMismatchedId()
    {
        var scope = CreateScope();

        var access = new FakeAccessReader
        {
            Scope = scope
        };

        var repository = new FakeRepository
        {
            FindResult = CreateEntry(scope)
        };

        var service = CreateService(access, repository);

        var result = await service.FindAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task EmptyNotificationId_ShouldSkipRepository()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };

        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        var found = await service.FindAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            Guid.Empty);

        var marked = await service.TryMarkReadAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            Guid.Empty,
            CreatedAt.AddMinutes(10));

        Assert.Null(found);
        Assert.False(marked);
        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.MarkReadCalls);
    }

    [Fact]
    public async Task MarkRead_ShouldForwardAuthorizedScopeAndUtc()
    {
        var scope = CreateScope();

        var access = new FakeAccessReader
        {
            Scope = scope
        };

        var repository = new FakeRepository
        {
            MarkResult = true
        };

        var service = CreateService(access, repository);

        var notificationId = Guid.NewGuid();

        var localReadAt = new DateTimeOffset(
            2026, 11, 16, 14, 45, 0,
            TimeSpan.FromMinutes(330));

        var result = await service.TryMarkReadAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            notificationId,
            localReadAt);

        Assert.True(result);
        Assert.Equal(1, repository.MarkReadCalls);
        Assert.Equal(scope, repository.LastScope);
        Assert.Equal(
            notificationId,
            repository.LastNotificationId);
        Assert.Equal(
            CreatedAt.AddMinutes(15),
            repository.LastReadAtUtc);
        Assert.Equal(
            TimeSpan.Zero,
            repository.LastReadAtUtc!.Value.Offset);
    }

    [Fact]
    public async Task MarkRead_ShouldReturnFalseWhenNotUpdated()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };

        var repository = new FakeRepository
        {
            MarkResult = false
        };

        var service = CreateService(access, repository);

        var result = await service.TryMarkReadAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            Guid.NewGuid(),
            CreatedAt.AddMinutes(10));

        Assert.False(result);
        Assert.Equal(1, repository.MarkReadCalls);
    }

    [Fact]
    public async Task MismatchedResolvedModule_ShouldDenyAllOperations()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope(NotificationModule.Hobbies)
        };

        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var list = await service.ListLatestAsync(
            accountId,
            NotificationModule.Social,
            10);

        var found = await service.FindAsync(
            accountId,
            NotificationModule.Social,
            notificationId);

        var marked = await service.TryMarkReadAsync(
            accountId,
            NotificationModule.Social,
            notificationId,
            CreatedAt.AddMinutes(10));

        Assert.Empty(list);
        Assert.Null(found);
        Assert.False(marked);

        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.MarkReadCalls);
    }

    [Fact]
    public async Task Cancellation_ShouldPreventRepositoryAccess()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };

        var repository = new FakeRepository();
        var service = CreateService(access, repository);

        using var source = new CancellationTokenSource();
        source.Cancel();

        var accountId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListLatestAsync(
                accountId,
                NotificationModule.Social,
                10,
                source.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FindAsync(
                accountId,
                NotificationModule.Social,
                notificationId,
                source.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TryMarkReadAsync(
                accountId,
                NotificationModule.Social,
                notificationId,
                CreatedAt.AddMinutes(10),
                source.Token));

        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.MarkReadCalls);
    }

    private sealed class FakeAccessReader
        : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? Scope { get; set; }

        public int Calls { get; private set; }

        public Task<NotificationModuleScope?>
            ResolveOwnedActiveScopeAsync(
                Guid authenticatedAccountId,
                NotificationModule module,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            return Task.FromResult(Scope);
        }
    }

    private sealed class FakeRepository
        : INotificationRepository
    {
        public IReadOnlyList<NotificationEntry> ListResult
        { get; set; } = Array.Empty<NotificationEntry>();

        public NotificationEntry? FindResult { get; set; }

        public bool MarkResult { get; set; } = true;

        public int ListCalls { get; private set; }

        public int FindCalls { get; private set; }

        public int MarkReadCalls { get; private set; }

        public int LastMaxItems { get; private set; }

        public NotificationModuleScope? LastScope
        { get; private set; }

        public Guid LastNotificationId { get; private set; }

        public DateTimeOffset? LastReadAtUtc
        { get; private set; }

        public Task AddAsync(
            NotificationEntry notification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<NotificationEntry?> FindAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FindCalls++;
            LastScope = recipientScope;
            LastNotificationId = notificationId;

            return Task.FromResult(FindResult);
        }

        public Task<IReadOnlyList<NotificationEntry>>
            ListLatestAsync(
                NotificationModuleScope recipientScope,
                int maxItems,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ListCalls++;
            LastScope = recipientScope;
            LastMaxItems = maxItems;

            return Task.FromResult(ListResult);
        }

        public Task<bool> TryMarkReadAsync(
            NotificationModuleScope recipientScope,
            Guid notificationId,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MarkReadCalls++;
            LastScope = recipientScope;
            LastNotificationId = notificationId;
            LastReadAtUtc = readAtUtc;

            return Task.FromResult(MarkResult);
        }
    }
}
