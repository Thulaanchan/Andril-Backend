using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>
/// Safe, isolated, disposable PostgreSQL database fixture for Social integration testing.
/// Strictly restricted to local loopback connections against the 'postgres' maintenance database.
/// Creates an unpredictable uniquely-named database with the 'andril_social_test_' prefix,
/// executes migrations, and drops only that specific created database upon disposal.
/// Cleanup failures are observable and never silently suppressed.
/// </summary>
public sealed class LocalSocialQuestionnaireDatabase : IAsyncDisposable
{
    public const string DatabasePrefix = "andril_social_test_";

    private readonly string _adminConnection;
    private readonly string _connection;
    private readonly string _databaseName;
    private readonly bool _databaseCreated;
    private int _disposed;

    public string DatabaseName => _databaseName;
    public string ConnectionString => _connection;
    public bool IsCreated => _databaseCreated;

    private LocalSocialQuestionnaireDatabase(
        string adminConnection,
        string connection,
        string databaseName,
        bool databaseCreated)
    {
        _adminConnection = adminConnection;
        _connection = connection;
        _databaseName = databaseName;
        _databaseCreated = databaseCreated;
    }

    /// <summary>
    /// Validates connection settings to guarantee strict local loopback isolation
    /// and prevent targeting production, remote, or non-maintenance databases.
    /// </summary>
    public static void ValidateSettings(NpgsqlConnectionStringBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("PostgreSQL host must be specified.");

        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException($"Remote or non-loopback host '{settings.Host}' is prohibited. Only local loopback connections are permitted.");

        if (!string.Equals(settings.Database, "postgres", StringComparison.Ordinal))
            throw new InvalidOperationException($"Maintenance database must be exactly 'postgres', found '{settings.Database}'.");
    }

    /// <summary>
    /// Creates a new disposable test database using the connection string configured in ANDRIL_M2_TEST_POSTGRES.
    /// </summary>
    public static async Task<LocalSocialQuestionnaireDatabase> CreateAsync(bool applySocialMigrations = true)
    {
        var rawConnection = Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection))
            throw new InvalidOperationException("ANDRIL_M2_TEST_POSTGRES environment variable is not configured.");

        var settings = new NpgsqlConnectionStringBuilder(rawConnection);
        ValidateSettings(settings);

        settings.Pooling = false;
        settings.IncludeErrorDetail = false;

        var admin = settings.ConnectionString;
        var uniqueName = DatabasePrefix + Guid.NewGuid().ToString("N");

        await using (var adminDb = new NpgsqlConnection(admin))
        {
            await adminDb.OpenAsync();
            await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{uniqueName}\"", adminDb);
            await createCmd.ExecuteNonQueryAsync();
        }

        settings.Database = uniqueName;
        var database = new LocalSocialQuestionnaireDatabase(
            admin,
            settings.ConnectionString,
            uniqueName,
            databaseCreated: true);

        try
        {
            if (applySocialMigrations)
            {
                await database.ApplyAccountsMigrationsAsync();
                await database.ApplySocialMigrationsAsync();
            }

            return database;
        }
        catch (Exception initEx)
        {
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception cleanupEx)
            {
                throw new AggregateException(
                    "Disposable test database initialization failed and secondary cleanup failed.",
                    initEx,
                    cleanupEx);
            }

            throw;
        }
    }

    /// <summary>
    /// Applies Social EF Core migrations to the disposable test database.
    /// </summary>
    public async Task ApplySocialMigrationsAsync()
    {
        await using var context = Open();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Applies Accounts EF Core migrations to support cross-module identity prerequisites.
    /// </summary>
    public async Task ApplyAccountsMigrationsAsync()
    {
        await using var context = OpenAccounts();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Opens an isolated <see cref="SocialDbContext"/> instance bound to this disposable database.
    /// </summary>
    public SocialDbContext Open(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(_connection, npgsql =>
            {
                npgsql.MigrationsHistoryTable(
                    SocialDbContextFactory.MigrationsHistoryTableName,
                    SocialDbContext.Schema);
            })
            .AddInterceptors(interceptors)
            .Options;

        return new SocialDbContext(options);
    }

    /// <summary>
    /// Opens an isolated <see cref="AccountsDbContext"/> instance bound to this disposable database.
    /// </summary>
    public AccountsDbContext OpenAccounts(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(_connection, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity");
            })
            .AddInterceptors(interceptors)
            .Options;

        return new AccountsDbContext(options);
    }

    /// <summary>
    /// Drops the generated disposable database with FORCE. Guarantees that only the database
    /// created by this instance is ever dropped. Cleanup failures are observable and reported.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_databaseCreated)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_databaseName) ||
            !_databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal) ||
            string.Equals(_databaseName, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Safety violation: Refusing to drop database with unsafe identifier '{_databaseName}'.");
        }

        await using var adminDb = new NpgsqlConnection(_adminConnection);
        await adminDb.OpenAsync();
        await using var dropCmd = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", adminDb);
        await dropCmd.ExecuteNonQueryAsync();
    }
}

public sealed class LocalSocialQuestionnaireDatabaseSafetyTests
{
    [Theory]
    [InlineData("db.supabase.co")]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.1")]
    [InlineData("remote-server.internal")]
    public void ValidateSettings_RejectsRemoteHosts(string remoteHost)
    {
        var settings = new NpgsqlConnectionStringBuilder($"Host={remoteHost};Database=postgres;Username=postgres;Password=test");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSocialQuestionnaireDatabase.ValidateSettings(settings));
        Assert.Contains("Remote or non-loopback host", ex.Message);
    }

    [Theory]
    [InlineData("andril_prod")]
    [InlineData("andril_dev")]
    [InlineData("template1")]
    [InlineData("social_db")]
    public void ValidateSettings_RejectsNonPostgresMaintenanceDatabase(string invalidDb)
    {
        var settings = new NpgsqlConnectionStringBuilder($"Host=localhost;Database={invalidDb};Username=postgres;Password=test");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSocialQuestionnaireDatabase.ValidateSettings(settings));
        Assert.Contains("Maintenance database must be exactly 'postgres'", ex.Message);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ValidateSettings_AcceptsLoopbackHosts(string loopbackHost)
    {
        var settings = new NpgsqlConnectionStringBuilder($"Host={loopbackHost};Database=postgres;Username=postgres;Password=test");
        LocalSocialQuestionnaireDatabase.ValidateSettings(settings);
    }
}
