
using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Andril.Persistence.Configurations;
using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// Database-free guard tests for PostgresNotificationRepository.
///
/// These tests verify input validation and cancellation.
/// They do NOT prove PostgreSQL row isolation, SQL
/// correctness, or recipient authorization.
/// </summary>
public sealed class PostgresNotificationRepositoryGuardTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AddAsync_SaveTaskFault_ShouldPropagateWithoutDatabaseConnection()
    {
        using var caller = new CancellationTokenSource();
        var failure = new InvalidOperationException("N37 synthetic save failure.");
        var saveTask = Task.FromException<int>(failure);
        var guard = new RejectDatabaseConnections();
        using var context = new ControlledSaveContext(guard, saveTask);
        var input = CreateSaveInput();
        var repository = new PostgresNotificationRepository(context);
        Task? operation = null;

        try
        {
            Assert.True(caller.Token.CanBeCanceled);
            Assert.False(caller.IsCancellationRequested);
            operation = repository.AddAsync(input.Notification, caller.Token);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(10)));

            var stateAfter = context.Entry(context.Notification!).State;
            WriteTrackingObservation("fault", context, stateAfter, guard, input.AccountId);
            Assert.Equal(TaskStatus.Faulted, saveTask.Status);
            Assert.Equal(TaskStatus.Faulted, operation.Status);
            Assert.Same(failure, thrown);
            Assert.Same(failure, Assert.Single(saveTask.Exception!.InnerExceptions));
            Assert.Equal(caller.Token, context.CallerToken);
            Assert.False(caller.IsCancellationRequested);
            AssertSaveBoundary(context, input.Notification, stateAfter, guard);
        }
        finally
        {
            await ObserveOperationAsync(operation);
        }
    }

    [Fact]
    public async Task AddAsync_IndependentCanceledSaveTask_ShouldCancelWithoutDatabaseConnection()
    {
        using var caller = new CancellationTokenSource();
        using var dependency = new CancellationTokenSource();
        dependency.Cancel();
        var saveTask = Task.FromCanceled<int>(dependency.Token);
        var guard = new RejectDatabaseConnections();
        using var context = new ControlledSaveContext(guard, saveTask);
        var input = CreateSaveInput();
        var repository = new PostgresNotificationRepository(context);
        Task? operation = null;

        try
        {
            Assert.True(caller.Token.CanBeCanceled);
            Assert.False(caller.IsCancellationRequested);
            Assert.NotEqual(caller.Token, dependency.Token);
            Assert.Equal(TaskStatus.Canceled, saveTask.Status);
            operation = repository.AddAsync(input.Notification, caller.Token);
            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(10)));

            var stateAfter = context.Entry(context.Notification!).State;
            WriteTrackingObservation("cancellation", context, stateAfter, guard, input.AccountId);
            Assert.Equal(TaskStatus.Canceled, saveTask.Status);
            Assert.Equal(TaskStatus.Canceled, operation.Status);
            Assert.Equal(dependency.Token, thrown.CancellationToken);
            Assert.Equal(caller.Token, context.CallerToken);
            Assert.False(context.CallerToken.IsCancellationRequested);
            Assert.False(caller.IsCancellationRequested);
            AssertSaveBoundary(context, input.Notification, stateAfter, guard);
        }
        finally
        {
            await ObserveOperationAsync(operation);
        }
    }

    private static (Guid AccountId, NotificationEntry Notification) CreateSaveInput()
    {
        // Account is synthetic caller context only: this repository accepts
        // an already-authorized scope, not an account or ownership grant.
        var accountId = Guid.Parse("37000000-0000-0000-0000-000000000001");
        var scope = new NotificationModuleScope(NotificationModule.Hobbies,
            Guid.Parse("37000000-0000-0000-0000-000000000002"));
        return (accountId, new NotificationEntry(
            Guid.Parse("37000000-0000-0000-0000-000000000003"), scope,
            "message", "N37 synthetic title", "N37 synthetic preview",
            new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero)));
    }

    private void WriteTrackingObservation(string scenario, ControlledSaveContext context,
        EntityState stateAfter, RejectDatabaseConnections guard, Guid accountId)
    {
        Assert.NotEqual(Guid.Empty, accountId);
        // Record actual observations before asserting the characterization.
        output.WriteLine($"N37_OBSERVATION scenario={scenario}; atSave={context.StateAtSave}; " +
            $"after={stateAfter}; saveCalls={context.SaveCalls}; connections={guard.Attempts}; " +
            $"syntheticAccountContext={accountId}; accountNotMapped=true");
    }

    private static void AssertSaveBoundary(ControlledSaveContext context,
        NotificationEntry input, EntityState stateAfter, RejectDatabaseConnections guard)
    {
        Assert.Equal(1, context.SaveCalls);
        var entry = Assert.Single(context.ChangeTracker.Entries<NotificationPersistenceRecord>());
        var record = Assert.IsType<NotificationPersistenceRecord>(context.Notification);
        Assert.Same(record, entry.Entity);
        Assert.Equal(input.Id, record.Id);
        Assert.Equal(input.RecipientScope.RecipientModuleProfileId, record.RecipientModuleProfileId);
        Assert.Equal("hobbies", record.Module);
        Assert.Equal(input.Type, record.Type);
        Assert.Equal(input.Title, record.Title);
        Assert.Equal(input.Preview, record.Preview);
        Assert.Equal(input.CreatedAtUtc, record.CreatedAtUtc);
        Assert.Null(record.ReadAtUtc);
        Assert.False(record.IsRead);
        Assert.Null(input.ReadAtUtc);
        Assert.False(input.IsRead);
        // These are in-memory EF states, not committed database state.
        Assert.Equal(EntityState.Added, context.StateAtSave);
        Assert.Equal(EntityState.Added, stateAfter);
        Assert.Equal(0, guard.Attempts);
    }

    private static async Task ObserveOperationAsync(Task? operation)
    {
        if (operation is null)
            return;
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _ = operation.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            // Observe expected failure/cancellation without hiding assertions.
        }
    }

    private sealed class ControlledSaveContext : DbContext
    {
        private readonly Task<int> saveTask;
        public int SaveCalls { get; private set; }
        public CancellationToken CallerToken { get; private set; }
        public NotificationPersistenceRecord? Notification { get; private set; }
        public EntityState? StateAtSave { get; private set; }

        public ControlledSaveContext(RejectDatabaseConnections guard, Task<int> saveTask)
            : base(new DbContextOptionsBuilder<ControlledSaveContext>()
                .UseNpgsql("Host=127.0.0.1;Database=andril_n37_offline")
                .AddInterceptors(guard).Options)
        {
            this.saveTask = saveTask;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(new NotificationPersistenceRecordConfiguration());
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            CallerToken = cancellationToken;
            var entry = ChangeTracker.Entries<NotificationPersistenceRecord>().Single();
            Notification = entry.Entity;
            StateAtSave = entry.State;
            // Never call base: no provider save or transaction is executed.
            return saveTask;
        }
    }

    private sealed class RejectDatabaseConnections : DbConnectionInterceptor
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);

        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("N37 forbids database connections.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("N37 forbids database connections.");
        }
    }

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
