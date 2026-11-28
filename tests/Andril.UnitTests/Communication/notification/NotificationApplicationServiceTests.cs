
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

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task AuthorizedList_ShouldReturnDtosAndCapPageSize(
        NotificationModule module)
    {
        var scope = CreateScope(module);
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
            module,
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

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task TryCreateAsync_ShouldAddForAuthorizedScope(
        NotificationModule module)
    {
        var scope = CreateScope(module);
        var access = new FakeAccessReader
        {
            Scope = scope
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);
        var notification = CreateEntry(scope);

        var created = await service.TryCreateAsync(
            Guid.NewGuid(),
            module,
            notification);

        Assert.True(created);
        Assert.Equal(1, access.Calls);
        Assert.Equal(1, repository.AddCalls);
        Assert.Same(notification, repository.LastAddedNotification);
    }

    [Fact]
    public async Task TryCreateAsync_ShouldDenyMismatchedProfile()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope(NotificationModule.Social)
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);
        var notification = CreateEntry(
            CreateScope(NotificationModule.Social));

        var created = await service.TryCreateAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            notification);

        Assert.False(created);
        Assert.Equal(1, access.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task TryCreateAsync_ShouldDenyInvalidOrReadNotification()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);
        var readNotification = CreateEntry(
            access.Scope!,
            readAt: CreatedAt.AddMinutes(1));

        Assert.False(await service.TryCreateAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            null!));

        Assert.False(await service.TryCreateAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            readNotification));

        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task TryCreateAsync_ShouldDenyWhenAuthorizationIsUnavailable()
    {
        var access = new FakeAccessReader();
        var repository = new FakeRepository();
        var service = CreateService(access, repository);
        var notification = CreateEntry(
            CreateScope(NotificationModule.Learning));

        var created = await service.TryCreateAsync(
            Guid.NewGuid(),
            NotificationModule.Learning,
            notification);

        Assert.False(created);
        Assert.Equal(1, access.Calls);
        Assert.Equal(0, repository.AddCalls);
    }

    [Fact]
    public async Task TryCreateAsync_ShouldPropagateRepositoryFailure()
    {
        var scope = CreateScope();
        var access = new FakeAccessReader
        {
            Scope = scope
        };
        var failure = new InvalidOperationException(
            "Notification persistence is unavailable.");
        var repository = new FakeRepository
        {
            AddException = failure
        };
        var service = CreateService(access, repository);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TryCreateAsync(
                Guid.NewGuid(),
                NotificationModule.Social,
                CreateEntry(scope)));

        Assert.Same(failure, thrown);
        Assert.Equal(1, repository.AddCalls);
    }

    [Fact]
    public async Task TryCreateAsync_CancellationShouldPreventAccessAndWrite()
    {
        var scope = CreateScope();
        var access = new FakeAccessReader
        {
            Scope = scope
        };
        var repository = new FakeRepository();
        var service = CreateService(access, repository);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TryCreateAsync(
                Guid.NewGuid(),
                NotificationModule.Social,
                CreateEntry(scope),
                source.Token));

        Assert.Equal(0, access.Calls);
        Assert.Equal(0, repository.AddCalls);
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
    public async Task DefaultReadTime_ShouldSkipRepository()
    {
        var access = new FakeAccessReader
        {
            Scope = CreateScope()
        };

        var repository = new FakeRepository
        {
            MarkResult = true
        };

        var service = CreateService(access, repository);

        var result = await service.TryMarkReadAsync(
            Guid.NewGuid(),
            NotificationModule.Social,
            Guid.NewGuid(),
            default);

        Assert.False(result);
        Assert.Equal(0, repository.MarkReadCalls);
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

    [Fact]
    public async Task ListLatestAsync_SameProfileIdDifferentModule_ShouldDenyEntirePage()
    {
        var profileId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var learningScope = new NotificationModuleScope(NotificationModule.Learning, profileId);
        var hobbiesScope = new NotificationModuleScope(NotificationModule.Hobbies, profileId);
        Assert.Equal(learningScope.RecipientModuleProfileId, hobbiesScope.RecipientModuleProfileId);
        Assert.NotEqual(learningScope.Module, hobbiesScope.Module);

        var learningNotification = CreateEntry(learningScope,
            Guid.Parse("22222222-2222-4222-8222-222222222222"));
        var hobbiesNotification = CreateEntry(hobbiesScope,
            Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var access = new FakeAccessReader { Scope = learningScope };
        var repository = new FakeRepository
        {
            // Deliberately contaminated: the fake returns both rows unchanged.
            ListResult = new[] { learningNotification, hobbiesNotification }
        };
        var service = CreateService(access, repository);

        var result = await service.ListLatestAsync(
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            NotificationModule.Learning, 10);

        Assert.Empty(result);
        Assert.Equal(2, repository.ListResult.Count);
        Assert.Same(learningNotification, repository.ListResult[0]);
        Assert.Same(hobbiesNotification, repository.ListResult[1]);
        Assert.Same(learningScope, repository.LastScope);
        Assert.Equal(1, access.Calls);
        Assert.Equal(1, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.AddCalls);
        Assert.Equal(0, repository.MarkReadCalls);
        Assert.Null(repository.LastAddedNotification);
        Assert.Null(repository.LastReadAtUtc);
        Assert.False(learningNotification.IsRead);
        Assert.Null(learningNotification.ReadAtUtc);
        Assert.False(hobbiesNotification.IsRead);
        Assert.Null(hobbiesNotification.ReadAtUtc);
    }

    [Fact]
    public async Task FindAsync_SameProfileIdDifferentModule_ShouldDenyMatchingId()
    {
        var profileId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var learningScope = new NotificationModuleScope(NotificationModule.Learning, profileId);
        var hobbiesScope = new NotificationModuleScope(NotificationModule.Hobbies, profileId);
        Assert.Equal(learningScope.RecipientModuleProfileId, hobbiesScope.RecipientModuleProfileId);
        Assert.NotEqual(learningScope.Module, hobbiesScope.Module);

        var requestedId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var hobbiesNotification = CreateEntry(hobbiesScope, requestedId);
        Assert.Equal(requestedId, hobbiesNotification.Id);
        var access = new FakeAccessReader { Scope = learningScope };
        var repository = new FakeRepository
        {
            // Exact requested ID, wrong module: only the service may reject it.
            FindResult = hobbiesNotification
        };
        var service = CreateService(access, repository);

        var result = await service.FindAsync(
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            NotificationModule.Learning, requestedId);

        Assert.Null(result);
        Assert.Same(hobbiesNotification, repository.FindResult);
        Assert.Same(learningScope, repository.LastScope);
        Assert.Equal(requestedId, repository.LastNotificationId);
        Assert.Equal(1, access.Calls);
        Assert.Equal(1, repository.FindCalls);
        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.AddCalls);
        Assert.Equal(0, repository.MarkReadCalls);
        Assert.Null(repository.LastAddedNotification);
        Assert.Null(repository.LastReadAtUtc);
        Assert.False(hobbiesNotification.IsRead);
        Assert.Null(hobbiesNotification.ReadAtUtc);
    }

    [Fact]
    public async Task MarkRead_AccessReaderFault_ShouldNotReachRepository()
    {
        var accountId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var notificationId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var failure = new InvalidOperationException("N32 synthetic authorization reader failure.");
        var access = new FakeAccessReader
        {
            Scope = new NotificationModuleScope(NotificationModule.Learning,
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            ResolveException = failure
        };
        var repository = new FakeRepository { MarkResult = true };
        var service = CreateService(access, repository);
        var readAtUtc = CreatedAt.AddMinutes(10);
        var cancellationToken = CancellationToken.None;
        Assert.Equal(TimeSpan.Zero, readAtUtc.Offset);
        Assert.False(cancellationToken.IsCancellationRequested);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TryMarkReadAsync(accountId, NotificationModule.Learning,
                notificationId, readAtUtc, cancellationToken));

        Assert.Same(failure, thrown);
        Assert.Equal(1, access.Calls);
        Assert.Equal(accountId, access.LastAccountId);
        Assert.Equal(NotificationModule.Learning, access.LastModule);
        var faultedTask = access.LastFaultedTask;
        Assert.NotNull(faultedTask);
        Assert.True(faultedTask.IsFaulted);
        Assert.Same(failure, Assert.Single(faultedTask.Exception!.InnerExceptions));
        Assert.Equal(0, repository.AddCalls);
        Assert.Equal(0, repository.ListCalls);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(0, repository.MarkReadCalls);
        Assert.Null(repository.LastAddedNotification);
        Assert.Null(repository.LastReadAtUtc);
        Assert.Null(repository.LastScope);
    }

    private sealed class FakeAccessReader
        : INotificationRecipientAccessReader
    {
        public NotificationModuleScope? Scope { get; set; }

        public Exception? ResolveException { get; set; }

        public Guid LastAccountId { get; private set; }

        public NotificationModule LastModule { get; private set; }

        public Task<NotificationModuleScope?>? LastFaultedTask { get; private set; }

        public int Calls { get; private set; }

        public Task<NotificationModuleScope?>
            ResolveOwnedActiveScopeAsync(
                Guid authenticatedAccountId,
                NotificationModule module,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastAccountId = authenticatedAccountId;
            LastModule = module;

            if (ResolveException is { } failure)
            {
                LastFaultedTask = Task.FromException<NotificationModuleScope?>(failure);
                return LastFaultedTask;
            }

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

        public int AddCalls { get; private set; }

        public int ListCalls { get; private set; }

        public int FindCalls { get; private set; }

        public Exception? AddException { get; set; }

        public NotificationEntry? LastAddedNotification
        { get; private set; }

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
            AddCalls++;
            LastAddedNotification = notification;

            return AddException is null
                ? Task.CompletedTask
                : Task.FromException(AddException);
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
