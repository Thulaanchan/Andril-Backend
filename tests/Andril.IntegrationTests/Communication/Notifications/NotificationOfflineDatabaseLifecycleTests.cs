
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Runtime.ExceptionServices;

namespace Andril.IntegrationTests.Communication.Notifications;

// Simulates database creation and deletion.
// No PostgreSQL connection or SQL execution.
internal sealed class SimulatedNotificationDatabase
{
    private readonly HashSet<string> databases =
        new(StringComparer.Ordinal);

    internal bool FailCreation { get; set; }
    internal bool LoseCreationAcknowledgement { get; set; }
    internal bool FailDeletion { get; set; }
    internal int CreateCalls { get; private set; }
    internal int DeleteCalls { get; private set; }

    internal bool Exists(string name) =>
        databases.Contains(name);

    internal Task CreateAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCalls++;

        if (FailCreation)
            throw new InvalidOperationException(
                "Simulated database creation failure.");

        if (!databases.Add(name))
            throw new InvalidOperationException(
                "Database already exists.");
        if (LoseCreationAcknowledgement)
        {
            throw new InvalidOperationException(
                "Simulated CREATE response lost.");
        }
        return Task.CompletedTask;
    }

    internal Task DeleteAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls++;

        if (FailDeletion)
            throw new InvalidOperationException(
                "Simulated database cleanup failure.");

        if (!databases.Remove(name))
            throw new InvalidOperationException(
                "Database does not exist.");

        return Task.CompletedTask;
    }
}

// Offline lifecycle prototype only.
// Never substitute its simulated operations
// for real SQL without separate safety review.
internal sealed class NotificationOfflineDatabaseLifecycle
{
    private readonly NotificationTestDatabaseOwnershipGate gate;
    private readonly SimulatedNotificationDatabase database;

    private int started;

    internal NotificationOfflineDatabaseLifecycle(
        NotificationTestDatabaseOwnershipGate gate,
        SimulatedNotificationDatabase database)
    {
        this.gate = gate ??
            throw new ArgumentNullException(nameof(gate));

        this.database = database ??
            throw new ArgumentNullException(nameof(database));
    }


    internal async Task RunAsync(
        Func<CancellationToken, Task> testBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testBody);

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException(
                "Fixture cannot run more than once.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await database.CreateAsync(
            gate.DatabaseName,
            cancellationToken);

        gate.RecordSuccessfulCreation(gate.DatabaseName);

        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            await testBody(cancellationToken);
        }
        catch (Exception error)
        {
            bodyFailure = error;
        }

        try
        {
            if (!gate.CanDelete(gate.DatabaseName))
            {
                throw new InvalidOperationException(
                    "Database cleanup ownership denied.");
            }

            await database.DeleteAsync(
                gate.DatabaseName,
                CancellationToken.None);

            gate.RecordSuccessfulDeletion(gate.DatabaseName);
        }
        catch (Exception error)
        {
            cleanupFailure = error;
        }

        if (bodyFailure is not null &&
            cleanupFailure is not null)
        {
            throw new AggregateException(
                "Test execution and database cleanup both failed.",
                bodyFailure,
                cleanupFailure);
        }

        if (bodyFailure is not null)
        {
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

}

public sealed class NotificationOfflineDatabaseLifecycleTests
{
    private const string Connection =
        "Host=127.0.0.1;" +
        "Database=postgres;" +
        "Username=fixture;" +
        "Password=dummy";

    private static NotificationTestDatabaseOwnershipGate
        CreateGate()
    {
        return Assert.IsType<NotificationTestDatabaseOwnershipGate>(
            NotificationTestDatabaseOwnershipGate.TryCreate(
                NotificationTestDatabaseOwnershipGate.RequiredOptIn,
                Connection));
    }

    [Fact]
    public async Task Success_ShouldCreateAndCleanup()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase();
        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        await fixture.RunAsync(_ => Task.CompletedTask);

        Assert.Equal(1, db.CreateCalls);
        Assert.Equal(1, db.DeleteCalls);
        Assert.False(db.Exists(gate.DatabaseName));
        Assert.False(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public async Task TestFailure_ShouldStillCleanup()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase();
        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunAsync(
                _ => Task.FromException(
                    new InvalidOperationException("Test failure."))));

        Assert.False(db.Exists(gate.DatabaseName));
        Assert.Equal(1, db.DeleteCalls);
    }

    [Fact]
    public async Task PreCancellation_ShouldNotCreate()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase();
        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.RunAsync(
                _ => Task.CompletedTask,
                cancellation.Token));

        Assert.Equal(0, db.CreateCalls);
        Assert.Equal(0, db.DeleteCalls);
    }

    [Fact]
    public async Task CancellationDuringTest_ShouldCleanup()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase();
        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.RunAsync(
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.False(db.Exists(gate.DatabaseName));
        Assert.Equal(1, db.DeleteCalls);
    }

    [Fact]
    public async Task CreationFailure_ShouldNotDelete()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase
        {
            FailCreation = true
        };

        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunAsync(_ => Task.CompletedTask));

        Assert.Equal(0, db.DeleteCalls);
        Assert.False(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public async Task DeletionFailure_ShouldNotConfirmDeletion()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase
        {
            FailDeletion = true
        };

        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunAsync(_ => Task.CompletedTask));

        Assert.True(db.Exists(gate.DatabaseName));
        Assert.True(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public async Task RepeatedExecution_ShouldBeRejected()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase();
        var fixture = new NotificationOfflineDatabaseLifecycle(gate, db);

        await fixture.RunAsync(_ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunAsync(_ => Task.CompletedTask));

        Assert.Equal(1, db.CreateCalls);
        Assert.Equal(1, db.DeleteCalls);
    }

    [Fact]
    public async Task LostCreationAcknowledgement_ShouldNotBlindDelete()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase
        {
            LoseCreationAcknowledgement = true
        };

        var fixture =
            new NotificationOfflineDatabaseLifecycle(gate, db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunAsync(_ => Task.CompletedTask));

        Assert.True(db.Exists(gate.DatabaseName));
        Assert.False(gate.CanDelete(gate.DatabaseName));
        Assert.Equal(0, db.DeleteCalls);
    }

    [Fact]
    public async Task TestAndCleanupFailure_ShouldPreserveBothErrors()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase
        {
            FailDeletion = true
        };

        var fixture =
            new NotificationOfflineDatabaseLifecycle(gate, db);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => fixture.RunAsync(
                _ => Task.FromException(
                    new InvalidOperationException("Test failed."))));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(
            "Test failed.",
            error.InnerExceptions[0].Message);

        Assert.Equal(
            "Simulated database cleanup failure.",
            error.InnerExceptions[1].Message);

        Assert.True(db.Exists(gate.DatabaseName));
        Assert.True(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public async Task CancellationAndCleanupFailure_ShouldPreserveBoth()
    {
        var gate = CreateGate();
        var db = new SimulatedNotificationDatabase
        {
            FailDeletion = true
        };

        var fixture =
            new NotificationOfflineDatabaseLifecycle(gate, db);

        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => fixture.RunAsync(
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(2, error.InnerExceptions.Count);

        Assert.IsAssignableFrom<OperationCanceledException>(
            error.InnerExceptions[0]);

        Assert.IsType<InvalidOperationException>(
            error.InnerExceptions[1]);

        Assert.True(db.Exists(gate.DatabaseName));
        Assert.True(gate.CanDelete(gate.DatabaseName));
    }

}
