
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class InMemoryNotificationRepositoryTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static NotificationModuleScope CreateScope(
        NotificationModule module = NotificationModule.Social,
        Guid? profileId = null)
    {
        return new NotificationModuleScope(
            module,
            profileId ?? Guid.NewGuid());
    }

    private static NotificationEntry CreateEntry(
        NotificationModuleScope scope,
        Guid? id = null,
        DateTimeOffset? createdAt = null)
    {
        return new NotificationEntry(
            id ?? Guid.NewGuid(),
            scope,
            "message",
            "New Message",
            "You received a message.",
            createdAt ?? CreatedAt);
    }

    [Fact]
    public async Task AddAndFind_ShouldReturnNotificationForExactScope()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();
        var entry = CreateEntry(scope);

        await repository.AddAsync(entry);

        var equivalentScope = new NotificationModuleScope(
            scope.Module,
            scope.RecipientModuleProfileId);

        var found = await repository.FindAsync(
            equivalentScope,
            entry.Id);

        Assert.NotNull(found);
        Assert.Equal(entry.Id, found.Id);
        Assert.Equal(entry.Title, found.Title);
        Assert.False(found.IsRead);
    }

    [Fact]
    public async Task SameModuleDifferentProfile_ShouldDenyAccess()
    {
        var repository = new InMemoryNotificationRepository();

        var ownerScope = CreateScope(NotificationModule.Social);
        var otherScope = CreateScope(NotificationModule.Social);

        var entry = CreateEntry(ownerScope);
        await repository.AddAsync(entry);

        var found = await repository.FindAsync(
            otherScope,
            entry.Id);

        var list = await repository.ListLatestAsync(
            otherScope,
            10);

        Assert.Null(found);
        Assert.Empty(list);
    }

    [Fact]
    public async Task SameProfileIdDifferentModule_ShouldDenyAccess()
    {
        var repository = new InMemoryNotificationRepository();

        var profileId = Guid.NewGuid();

        var learningScope = CreateScope(
            NotificationModule.Learning,
            profileId);

        var hobbiesScope = CreateScope(
            NotificationModule.Hobbies,
            profileId);

        var entry = CreateEntry(learningScope);

        await repository.AddAsync(entry);

        Assert.Null(await repository.FindAsync(
            hobbiesScope,
            entry.Id));

        Assert.Empty(await repository.ListLatestAsync(
            hobbiesScope,
            10));
    }

    [Fact]
    public async Task DuplicateNotificationId_ShouldBeRejected()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();

        var id = Guid.NewGuid();

        await repository.AddAsync(CreateEntry(scope, id));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await repository.AddAsync(
                    CreateEntry(scope, id));
            });

        var notifications =
            await repository.ListLatestAsync(scope, 10);

        Assert.Single(notifications);
    }

    [Fact]
    public async Task ListLatest_ShouldFilterSortAndLimitResults()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();

        var oldest = CreateEntry(
            scope,
            createdAt: CreatedAt.AddMinutes(-10));

        var middle = CreateEntry(
            scope,
            createdAt: CreatedAt);

        var newest = CreateEntry(
            scope,
            createdAt: CreatedAt.AddMinutes(10));

        var foreign = CreateEntry(
            CreateScope(NotificationModule.Hobbies),
            createdAt: CreatedAt.AddHours(1));

        await repository.AddAsync(middle);
        await repository.AddAsync(foreign);
        await repository.AddAsync(oldest);
        await repository.AddAsync(newest);

        var results = await repository.ListLatestAsync(
            scope,
            2);

        Assert.Equal(2, results.Count);
        Assert.Equal(newest.Id, results[0].Id);
        Assert.Equal(middle.Id, results[1].Id);
    }

    [Fact]
    public async Task ListLatest_ShouldReturnEmptyWhenNoItemsExist()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();

        var results = await repository.ListLatestAsync(
            scope,
            10);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListLatest_ShouldRejectInvalidPageSize(
        int maxItems)
    {
        var repository = new InMemoryNotificationRepository();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () =>
            {
                await repository.ListLatestAsync(
                    CreateScope(),
                    maxItems);
            });
    }

    [Fact]
    public async Task MarkRead_ShouldBeIdempotent()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();
        var entry = CreateEntry(scope);

        await repository.AddAsync(entry);

        var firstRead = CreatedAt.AddMinutes(5);
        var secondRead = CreatedAt.AddMinutes(20);

        var firstResult = await repository.TryMarkReadAsync(
            scope,
            entry.Id,
            firstRead);

        var secondResult = await repository.TryMarkReadAsync(
            scope,
            entry.Id,
            secondRead);

        var found = await repository.FindAsync(
            scope,
            entry.Id);

        Assert.True(firstResult);
        Assert.True(secondResult);

        Assert.NotNull(found);
        Assert.True(found.IsRead);
        Assert.Equal(firstRead, found.ReadAtUtc);
    }

    [Fact]
    public async Task MarkRead_ShouldDenyOtherProfile()
    {
        var repository = new InMemoryNotificationRepository();

        var ownerScope = CreateScope();
        var foreignScope = CreateScope();

        var entry = CreateEntry(ownerScope);

        await repository.AddAsync(entry);

        var result = await repository.TryMarkReadAsync(
            foreignScope,
            entry.Id,
            CreatedAt.AddMinutes(10));

        Assert.False(result);
        Assert.False(entry.IsRead);
        Assert.Null(entry.ReadAtUtc);
    }

    [Fact]
    public async Task MarkRead_ShouldDenyOtherModule()
    {
        var repository = new InMemoryNotificationRepository();

        var ownerScope = CreateScope(NotificationModule.Social);
        var otherScope = CreateScope(NotificationModule.Learning);

        var entry = CreateEntry(ownerScope);

        await repository.AddAsync(entry);

        var result = await repository.TryMarkReadAsync(
            otherScope,
            entry.Id,
            CreatedAt.AddMinutes(10));

        Assert.False(result);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public async Task MarkRead_ShouldReturnFalseForMissingNotification()
    {
        var repository = new InMemoryNotificationRepository();

        var result = await repository.TryMarkReadAsync(
            CreateScope(),
            Guid.NewGuid(),
            CreatedAt.AddMinutes(5));

        Assert.False(result);
    }

    [Fact]
    public async Task MarkRead_ShouldRejectTimestampBeforeCreation()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();
        var entry = CreateEntry(scope);

        await repository.AddAsync(entry);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () =>
            {
                await repository.TryMarkReadAsync(
                    scope,
                    entry.Id,
                    CreatedAt.AddMinutes(-1));
            });

        Assert.False(entry.IsRead);
        Assert.Null(entry.ReadAtUtc);
    }

    [Fact]
    public void NullArguments_ShouldBeRejected()
    {
        var repository = new InMemoryNotificationRepository();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = repository.AddAsync(null!);
        });

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = repository.FindAsync(
                null!,
                Guid.NewGuid());
        });

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = repository.ListLatestAsync(null!, 10);
        });

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = repository.TryMarkReadAsync(
                null!,
                Guid.NewGuid(),
                CreatedAt);
        });
    }

    [Fact]
    public async Task CancelledOperations_ShouldNotChangeData()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();

        var existing = CreateEntry(scope);
        await repository.AddAsync(existing);

        var newEntry = CreateEntry(scope);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await repository.AddAsync(
                    newEntry,
                    cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await repository.FindAsync(
                    scope,
                    existing.Id,
                    cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await repository.ListLatestAsync(
                    scope,
                    10,
                    cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await repository.TryMarkReadAsync(
                    scope,
                    existing.Id,
                    CreatedAt.AddMinutes(10),
                    cancellation.Token);
            });

        Assert.Null(await repository.FindAsync(
            scope,
            newEntry.Id));

        Assert.False(existing.IsRead);

        var remaining =
            await repository.ListLatestAsync(scope, 10);

        Assert.Single(remaining);
    }

    [Fact]
    public async Task ConcurrentDuplicateAdds_ShouldSaveExactlyOne()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();
        var id = Guid.NewGuid();

        var attempts = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await repository.AddAsync(
                        CreateEntry(scope, id));

                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(success => success));

        var notifications =
            await repository.ListLatestAsync(scope, 100);

        Assert.Single(notifications);
        Assert.Equal(id, notifications[0].Id);
    }

    [Fact]
    public async Task SameTimestamp_ShouldSortByIdDescending()
    {
        var repository = new InMemoryNotificationRepository();
        var scope = CreateScope();

        var smallerId = Guid.Parse(
            "00000000-0000-0000-0000-000000000001");

        var largerId = Guid.Parse(
            "00000000-0000-0000-0000-000000000002");

        await repository.AddAsync(
            CreateEntry(scope, smallerId));

        await repository.AddAsync(
            CreateEntry(scope, largerId));

        var results =
            await repository.ListLatestAsync(scope, 10);

        Assert.Equal(
            new[] { largerId, smallerId },
            results.Select(entry => entry.Id).ToArray());
    }
}
