
using System;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

// Test-only guard. Does not open a database connection.
internal static class NotificationPostgresConnectionPolicy
{
    internal static bool TryNormalize(
        string? configured,
        out string safeConnection)
    {
        safeConnection = string.Empty;

        if (string.IsNullOrWhiteSpace(configured))
            return false;

        try
        {
            var settings =
                new NpgsqlConnectionStringBuilder(configured);

            bool local =
                string.Equals(
                    settings.Host,
                    "localhost",
                    StringComparison.OrdinalIgnoreCase) ||
                settings.Host == "127.0.0.1" ||
                settings.Host == "::1";

            if (!local)
                return false;

            if (settings.Database != "postgres")
                return false;

            if (string.IsNullOrWhiteSpace(settings.Username))
                return false;

            settings.Pooling = false;
            settings.Enlist = false;

            safeConnection = settings.ConnectionString;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed class NotificationPostgresConnectionPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingConfiguration_ShouldBeRejected(
        string? configured)
    {
        var accepted =
            NotificationPostgresConnectionPolicy.TryNormalize(
                configured,
                out var connection);

        Assert.False(accepted);
        Assert.Equal(string.Empty, connection);
    }

    [Theory]
    [InlineData(
        "Host=example.com;Database=postgres;Username=test")]
    [InlineData(
        "Host=localhost;Database=production;Username=test")]
    [InlineData(
        "Host=localhost,example.com;Database=postgres;Username=test")]
    [InlineData(
        "Host=/var/run/postgresql;Database=postgres;Username=test")]
    [InlineData(
        "Host=localhost;Username=test")]
    public void UnsafeConfiguration_ShouldBeRejected(
        string configured)
    {
        var accepted =
            NotificationPostgresConnectionPolicy.TryNormalize(
                configured,
                out var connection);

        Assert.False(accepted);
        Assert.Equal(string.Empty, connection);
    }

    [Fact]
    public void LocalMaintenanceConnection_ShouldBeNormalized()
    {
        const string configured =
            "Host=127.0.0.1;" +
            "Database=postgres;" +
            "Username=fixture;" +
            "Password=dummy;" +
            "Pooling=true;Enlist=true";

        var accepted =
            NotificationPostgresConnectionPolicy.TryNormalize(
                configured,
                out var connection);

        Assert.True(accepted);

        var settings =
            new NpgsqlConnectionStringBuilder(connection);

        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal("postgres", settings.Database);
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
    }
}
