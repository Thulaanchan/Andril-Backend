
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// Test-only PostgreSQL fixture preparation.
/// No database connection, SQL, migrations, or cleanup operations.
/// </summary>
internal sealed class NotificationPostgresTestFixture
{
    private readonly NotificationTestDatabaseOwnershipGate _gate;

    private NotificationPostgresTestFixture(
        NotificationTestDatabaseOwnershipGate gate)
    {
        _gate = gate;
    }

    internal string DatabaseName => _gate.DatabaseName;

    internal bool IsDeletionAuthorized =>
        _gate.CanDelete(DatabaseName);

    internal static NotificationPostgresTestFixture? TryPrepare(
        string? optIn,
        string? connectionString)
    {
        var gate = NotificationTestDatabaseOwnershipGate.TryCreate(
            optIn,
            connectionString);

        return gate is null
            ? null
            : new NotificationPostgresTestFixture(gate);
    }
}

/// <summary>
/// Offline verification of fixture preparation.
/// Uses dummy connection settings and never opens PostgreSQL.
/// </summary>
public sealed class NotificationPostgresTestFixtureTests
{
    private const string LocalConnection =
        "Host=127.0.0.1;Database=postgres;" +
        "Username=fixture;Password=dummy";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INVALID_OPT_IN")]
    public void MissingOrInvalidOptIn_ShouldReject(
        string? optIn)
    {
        var fixture = NotificationPostgresTestFixture.TryPrepare(
            optIn,
            LocalConnection);

        Assert.Null(fixture);
    }

    [Theory]
    [InlineData(
        "Host=example.com;Database=postgres;Username=test")]
    [InlineData(
        "Host=localhost;Database=production;Username=test")]
    public void UnsafeConnection_ShouldReject(
        string connection)
    {
        var fixture = NotificationPostgresTestFixture.TryPrepare(
            NotificationTestDatabaseOwnershipGate.RequiredOptIn,
            connection);

        Assert.Null(fixture);
    }

    [Fact]
    public void SafeLocalPreparation_ShouldRemainNonDestructive()
    {
        var fixture = NotificationPostgresTestFixture.TryPrepare(
            NotificationTestDatabaseOwnershipGate.RequiredOptIn,
            LocalConnection);

        Assert.NotNull(fixture);

        Assert.True(
            NotificationTestDatabaseNamePolicy.IsValidName(
                fixture.DatabaseName));

        // No database has been created or confirmed.
        Assert.False(fixture.IsDeletionAuthorized);
    }
}

