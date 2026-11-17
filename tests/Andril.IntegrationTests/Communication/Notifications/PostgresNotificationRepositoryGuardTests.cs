
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// Database-free guard tests for PostgresNotificationRepository.
///
/// These tests verify input validation and cancellation.
/// They do NOT prove PostgreSQL row isolation, SQL
/// correctness, or recipient authorization.
/// </summary>
public sealed class PostgresNotificationRepositoryGuardTests
{
    private static DbContext CreateContext()
    {
        // Intentionally no database provider.
        // Only test paths that must return before accessing DB.
        var options = new DbContextOptionsBuilder<DbContext>()
            .Options;

        return new DbContext(options);
    }

    private static NotificationModuleScope CreateScope()
    {
        return new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());
    }

    [Fact]
    public void Constructor_ShouldRejectNullDbContext()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PostgresNotificationRepository(null!);
        });
    }

    [Fact]
    public async Task AddAsync_ShouldRejectNullNotification()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.AddAsync(null!));
    }

    [Fact]
    public async Task AddAsync_ShouldRespectCancellation()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.AddAsync(
                null!,
                cancellation.Token));
    }

    [Fact]
    public async Task FindAsync_ShouldRejectNullScope()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.FindAsync(
                null!,
                Guid.NewGuid()));
    }

    [Fact]
    public async Task FindAsync_ShouldReturnNullForEmptyId()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        var result = await repository.FindAsync(
            CreateScope(),
            Guid.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_ShouldRespectCancellation()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.FindAsync(
                CreateScope(),
                Guid.NewGuid(),
                cancellation.Token));
    }

    [Fact]
    public async Task ListLatestAsync_ShouldRejectNullScope()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.ListLatestAsync(
                null!,
                10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListLatestAsync_ShouldRejectInvalidPageSize(
        int maxItems)
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListLatestAsync(
                CreateScope(),
                maxItems));
    }

    [Fact]
    public async Task ListLatestAsync_ShouldRespectCancellation()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.ListLatestAsync(
                CreateScope(),
                10,
                cancellation.Token));
    }

    [Fact]
    public async Task TryMarkReadAsync_ShouldRejectNullScope()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.TryMarkReadAsync(
                null!,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task TryMarkReadAsync_ShouldReturnFalseForEmptyId()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        var result = await repository.TryMarkReadAsync(
            CreateScope(),
            Guid.Empty,
            DateTimeOffset.UtcNow);

        Assert.False(result);
    }

    [Fact]
    public async Task TryMarkReadAsync_ShouldRespectCancellation()
    {
        using var context = CreateContext();

        var repository =
            new PostgresNotificationRepository(context);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.TryMarkReadAsync(
                CreateScope(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                cancellation.Token));
    }
}
