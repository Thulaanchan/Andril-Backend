
using System;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

// Test-only lifecycle gate.
// Does not connect to PostgreSQL or execute SQL.
internal sealed class NotificationTestDatabaseOwnershipGate
{
    internal const string RequiredOptIn =
        "ALLOW_LOCAL_M4_DATABASE_CREATE_DROP";

    private readonly object sync = new();

    private bool creationConfirmed;
    private bool deletionConfirmed;

    private NotificationTestDatabaseOwnershipGate(
        string maintenanceConnection)
    {
        MaintenanceConnectionString = maintenanceConnection;

        DatabaseName =
            NotificationTestDatabaseNamePolicy.CreateNewName();
    }

    internal string DatabaseName { get; }

    internal string MaintenanceConnectionString { get; }

    internal static NotificationTestDatabaseOwnershipGate? TryCreate(
        string? optIn,
        string? connectionString)
    {
        if (!string.Equals(
                optIn,
                RequiredOptIn,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!NotificationPostgresConnectionPolicy.TryNormalize(
                connectionString,
                out var safeConnection))
        {
            return null;
        }

        return new NotificationTestDatabaseOwnershipGate(
            safeConnection);
    }

    // Must be called only AFTER CREATE DATABASE succeeds.
    // This method does not independently prove SQL execution.
    internal void RecordSuccessfulCreation(string? createdName)
    {
        lock (sync)
        {
            if (creationConfirmed ||
                deletionConfirmed ||
                !NotificationTestDatabaseNamePolicy
                    .MatchesCreatedName(
                        createdName,
                        DatabaseName))
            {
                throw new InvalidOperationException(
                    "Database creation cannot be confirmed.");
            }

            creationConfirmed = true;
        }
    }

    internal bool CanDelete(string? requestedName)
    {
        lock (sync)
        {
            return creationConfirmed &&
                   !deletionConfirmed &&
                   NotificationTestDatabaseNamePolicy
                       .MatchesCreatedName(
                           requestedName,
                           DatabaseName);
        }
    }

    // Must be called only AFTER DROP DATABASE succeeds.
    internal void RecordSuccessfulDeletion(string? deletedName)
    {
        lock (sync)
        {
            if (!creationConfirmed ||
                deletionConfirmed ||
                !NotificationTestDatabaseNamePolicy
                    .MatchesCreatedName(
                        deletedName,
                        DatabaseName))
            {
                throw new InvalidOperationException(
                    "Database deletion cannot be confirmed.");
            }

            deletionConfirmed = true;
        }
    }
}

public sealed class NotificationTestDatabaseOwnershipGateTests
{
    private const string LocalConnection =
        "Host=127.0.0.1;" +
        "Database=postgres;" +
        "Username=fixture;" +
        "Password=dummy;" +
        "Pooling=true;Enlist=true";

    private static NotificationTestDatabaseOwnershipGate
        CreateGate()
    {
        return Assert.IsType<NotificationTestDatabaseOwnershipGate>(
            NotificationTestDatabaseOwnershipGate.TryCreate(
                NotificationTestDatabaseOwnershipGate.RequiredOptIn,
                LocalConnection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("ALLOW_LOCAL_M4_DATABASE_CREATE_DROP ")]
    public void MissingOrInvalidOptIn_ShouldBeRejected(
        string? optIn)
    {
        Assert.Null(
            NotificationTestDatabaseOwnershipGate.TryCreate(
                optIn,
                LocalConnection));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Host=example.com;Database=postgres;Username=test")]
    [InlineData("Host=localhost;Database=production;Username=test")]
    public void UnsafeConnection_ShouldBeRejected(
        string connection)
    {
        Assert.Null(
            NotificationTestDatabaseOwnershipGate.TryCreate(
                NotificationTestDatabaseOwnershipGate.RequiredOptIn,
                connection));
    }

    [Fact]
    public void BeforeCreation_ShouldDenyDeletion()
    {
        var gate = CreateGate();

        Assert.False(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public void SuccessfulCreation_ShouldAllowExactDatabase()
    {
        var gate = CreateGate();

        gate.RecordSuccessfulCreation(gate.DatabaseName);

        Assert.True(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public void ForeignDatabase_ShouldBeDenied()
    {
        var gate = CreateGate();

        gate.RecordSuccessfulCreation(gate.DatabaseName);

        var foreignName =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.False(gate.CanDelete(foreignName));
    }

    [Fact]
    public void DifferentFixture_ShouldNotOwnDatabase()
    {
        var first = CreateGate();
        var second = CreateGate();

        first.RecordSuccessfulCreation(first.DatabaseName);

        Assert.False(second.CanDelete(first.DatabaseName));
    }

    [Fact]
    public void WrongCreationName_ShouldBeRejected()
    {
        var gate = CreateGate();

        Assert.Throws<InvalidOperationException>(() =>
            gate.RecordSuccessfulCreation("production"));

        Assert.False(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public void DuplicateCreationConfirmation_ShouldBeRejected()
    {
        var gate = CreateGate();

        gate.RecordSuccessfulCreation(gate.DatabaseName);

        Assert.Throws<InvalidOperationException>(() =>
            gate.RecordSuccessfulCreation(gate.DatabaseName));
    }

    [Fact]
    public void DeletionBeforeCreation_ShouldBeRejected()
    {
        var gate = CreateGate();

        Assert.Throws<InvalidOperationException>(() =>
            gate.RecordSuccessfulDeletion(gate.DatabaseName));
    }

    [Fact]
    public void AfterDeletion_ShouldDenyFurtherDeletion()
    {
        var gate = CreateGate();

        gate.RecordSuccessfulCreation(gate.DatabaseName);

        gate.RecordSuccessfulDeletion(gate.DatabaseName);

        Assert.False(gate.CanDelete(gate.DatabaseName));
    }

    [Fact]
    public void Connection_ShouldUseSafeNormalization()
    {
        var gate = CreateGate();

        var settings =
            new Npgsql.NpgsqlConnectionStringBuilder(
                gate.MaintenanceConnectionString);

        Assert.Equal("postgres", settings.Database);
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
    }
}
