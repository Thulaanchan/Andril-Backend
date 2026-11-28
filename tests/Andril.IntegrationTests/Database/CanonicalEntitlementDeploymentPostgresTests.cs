using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class CanonicalEntitlementDeploymentPostgresTests
{
    [LocalPostgresFact]
    public async Task IntegratedWriterResolvesTheSubjectAndRejectsUnprivilegedAndStaleTransitions()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync(withEntitlementSource: false);
        await InstallAsync(fixture);
        var owner = await fixture.SeedAsync("hobbies");
        var originalEvent = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddDays(1);

        var unauthorized = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(fixture, owner.Subject, originalEvent, null, 1, expiry, fixture.Role));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, unauthorized.SqlState);

        await using var db = fixture.Open();
        var grants = $"""
            GRANT USAGE ON SCHEMA andril_economy TO {fixture.Role};
            GRANT EXECUTE ON FUNCTION andril_economy.apply_subscription_transition(uuid, uuid, timestamptz, integer, timestamptz, text, text)
                TO {fixture.Role};
            """;
        await db.Database.ExecuteSqlRawAsync(grants);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(fixture, owner.Account, originalEvent, null, 1, expiry, fixture.Role))).SqlState);
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, (await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(fixture, owner.Subject, originalEvent, null, 1, DateTimeOffset.UtcNow.AddDays(-1), fixture.Role))).SqlState);

        var revision = await ApplyAsync(fixture, owner.Subject, originalEvent, null, 1, expiry, fixture.Role);
        Assert.Equal(revision, await ApplyAsync(fixture, owner.Subject, originalEvent, null, 1, expiry, fixture.Role));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(fixture, owner.Subject, originalEvent, null, 2, expiry, fixture.Role))).SqlState);

        var candidates = await Task.WhenAll(AttemptAsync(0, null), AttemptAsync(2, expiry));
        var winner = Assert.Single(candidates.OfType<DateTimeOffset>());
        Assert.Single(candidates, result => result is null);
        Assert.True(winner > revision);
        Assert.Equal(revision, await ApplyAsync(fixture, owner.Subject, originalEvent, null, 1, expiry, fixture.Role));
        Assert.Equal(PostgresErrorCodes.SerializationFailure, (await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(fixture, owner.Subject, Guid.NewGuid(), revision, 0, null, fixture.Role))).SqlState);
        Assert.Equal(winner.UtcDateTime, await db.Database.SqlQuery<DateTime>($"""
            SELECT updated_at_utc AS "Value" FROM andril_economy.premium_entitlements WHERE account_id = {owner.Account}
            """).SingleAsync());
        Assert.Equal(2L, await db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM andril_economy.premium_entitlement_transitions WHERE account_id = {owner.Account}
            """).SingleAsync());

        async Task<DateTimeOffset?> AttemptAsync(int tier, DateTimeOffset? end)
        {
            try { return await ApplyAsync(fixture, owner.Subject, Guid.NewGuid(), revision, tier, end, fixture.Role); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure) { return null; }
        }
    }

    internal static async Task InstallAsync(SharedGroupsDatabase fixture)
    {
        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "install-canonical-entitlements.sql"));
        await using var db = fixture.Open();
        await db.Database.ExecuteSqlRawAsync(ddl);
    }

    internal static async Task<DateTimeOffset> ApplyAsync(SharedGroupsDatabase fixture, Guid subject, Guid eventId,
        DateTimeOffset? expectedRevision, int tier, DateTimeOffset? expiry, string? executorRole = null,
        NpgsqlConnection? transactionConnection = null, NpgsqlTransaction? existingTransaction = null)
    {
        await using var ownedConnection = transactionConnection is null ? new NpgsqlConnection(fixture.Database.ConnectionString) : null;
        var connection = transactionConnection ?? ownedConnection!;
        if (connection.State == ConnectionState.Closed) await connection.OpenAsync();
        await using var ownedTransaction = existingTransaction is null ? await connection.BeginTransactionAsync() : null;
        var transaction = existingTransaction ?? ownedTransaction!;
        if (executorRole is not null)
        {
            await using var roleCommand = new NpgsqlCommand("SELECT set_config('role', $1, true)", connection, transaction);
            roleCommand.Parameters.AddWithValue(executorRole);
            await roleCommand.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("""
            SELECT andril_economy.apply_subscription_transition($1, $2, $3, $4, $5, 'verified-provisioning', 'fixture-evidence')
            """, connection, transaction);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)expectedRevision ?? DBNull.Value);
        command.Parameters.AddWithValue(tier);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)expiry ?? DBNull.Value);
        var result = (DateTime)(await command.ExecuteScalarAsync())!;
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync();
        return new(result);
    }
}
