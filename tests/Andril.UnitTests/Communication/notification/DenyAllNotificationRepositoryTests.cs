
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class DenyAllNotificationRepositoryTests
{
    private readonly DenyAllNotificationRepository _repository = new();

    private static NotificationModuleScope CreateScope() =>
        new(NotificationModule.Learning, Guid.NewGuid());

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task FindAsync_ShouldNeverExposeNotification(
        NotificationModule module)
    {
        var scope = new NotificationModuleScope(
            module, Guid.NewGuid());

        var result = await _repository.FindAsync(
            scope, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ListLatestAsync_ShouldAlwaysBeEmpty()
    {
        var result = await _repository.ListLatestAsync(
            CreateScope(), 20);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task TryMarkReadAsync_ShouldNeverReportSuccess()
    {
        var result = await _repository.TryMarkReadAsync(
            CreateScope(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        Assert.False(result);
    }

    [Fact]
    public async Task AddAsync_ShouldRejectWrites()
    {
        var scope = CreateScope();

        var entry = new NotificationEntry(
            Guid.NewGuid(),
            scope,
            "message",
            "New Message",
            null,
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.AddAsync(entry));
    }

    [Fact]
    public async Task FindAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.FindAsync(
                CreateScope(),
                Guid.NewGuid(),
                cts.Token));
    }

    [Fact]
    public async Task ListLatestAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.ListLatestAsync(
                CreateScope(),
                20,
                cts.Token));
    }

    [Fact]
    public async Task TryMarkReadAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.TryMarkReadAsync(
                CreateScope(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                cts.Token));
    }

    [Fact]
    public async Task AddAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _repository.AddAsync(
                null!,
                cts.Token));
    }
}
