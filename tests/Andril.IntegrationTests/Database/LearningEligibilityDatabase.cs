using System.Security.Cryptography;
using Npgsql;

namespace Andril.IntegrationTests.Database;

/// <summary>Use actual FORCE RLS even when CI supplies a superuser maintenance
/// connection. The temporary provisioner is separate from the restricted runtime.
/// No shared environment mutation, global default change, persistent service or secret log.</summary>
internal sealed class LearningEligibilityDatabase(SharedGroupsDatabase groups,string? cleanupConnection,string? role):IAsyncDisposable
{
    internal SharedGroupsDatabase Groups { get; }=groups;
    internal static async Task<LearningEligibilityDatabase> CreateAsync()
    {
        var raw=Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES")??throw new InvalidOperationException("Local PostgreSQL fixture is unavailable.");
        var settings=new NpgsqlConnectionStringBuilder(raw);LocalSocialQuestionnaireDatabase.ValidateSettings(settings);
        settings.Pooling=false;settings.IncludeErrorDetail=false;
        await using var admin=new NpgsqlConnection(settings.ConnectionString);await admin.OpenAsync();
        // Always exercise the same non-bypass migration-owner setup locally and in CI.
        var name="learning_fixture_"+Guid.NewGuid().ToString("N");
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var quoting=new NpgsqlCommandBuilder();var quoted=quoting.QuoteIdentifier(name);
        // Both identifiers and password are generated here, not supplied by an HTTP client.
        await using(var create=new NpgsqlCommand($"CREATE ROLE {quoted} LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS CREATEDB CREATEROLE PASSWORD '{password}'",admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var isolated=new NpgsqlConnectionStringBuilder(settings.ConnectionString){Username=name,Password=password};
            using var connectionScope=LocalSocialQuestionnaireDatabase.UseFixtureConnection(isolated.ConnectionString);
            return new(await SharedGroupsDatabase.CreateAsync(),settings.ConnectionString,name);
        }
        catch
        {
            await using var drop=new NpgsqlCommand($"DROP ROLE {quoted}",admin);await drop.ExecuteNonQueryAsync();throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try{await Groups.DisposeAsync();}
        finally
        {
            if(role is not null && cleanupConnection is not null)
            {
                await using var admin=new NpgsqlConnection(cleanupConnection);await admin.OpenAsync();
                using var quoting=new NpgsqlCommandBuilder();
                await using var drop=new NpgsqlCommand("DROP ROLE "+quoting.QuoteIdentifier(role),admin);await drop.ExecuteNonQueryAsync();
            }
        }
    }
    public override string ToString()=>"Disposable eligibility database [credentials omitted]";
}
