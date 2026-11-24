using System.Data;
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Transaction = System.Transactions.Transaction;

namespace Andril.Persistence.Repositories.Hobbies;

// Deliberately unregistered. The supplied data source is externally owned and must target
// the approved authoritative database. Pooling is allowed; sharing a caller's connection is not.
internal sealed class MapContributionCommittedReader : IMapContributionCommittedReader
{
    private readonly NpgsqlDataSource _dataSource;

    internal MapContributionCommittedReader(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        if (settings.Enlist || settings.Multiplexing || settings.NoResetOnClose)
            throw new ArgumentException(
                "Committed reads require auto-enlistment and multiplexing disabled, with connection reset enabled.", nameof(dataSource));
        _dataSource = dataSource;
    }

    public async Task<MapContribution?> FindOwnedByCaptureSessionAsync(Guid captureSessionId,
        Guid ownerHobbiesModuleProfileId, Guid attractionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Transaction.Current is not null)
            throw new InvalidOperationException("Committed contribution reads do not accept ambient transactions.");
        if (captureSessionId == Guid.Empty || ownerHobbiesModuleProfileId == Guid.Empty || attractionId == Guid.Empty)
            return null;
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options;
            await using var context = new HobbiesMapDbContext(options);
            await context.Database.UseTransactionAsync(transaction, cancellationToken);
            var contribution = await context.MapContributions.AsNoTracking().SingleOrDefaultAsync(row =>
                row.CaptureSessionId == captureSessionId
                && row.OwnerHobbiesModuleProfileId == ownerHobbiesModuleProfileId
                && row.AttractionId == attractionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return contribution;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Do not turn provider/mapping failures into missing rows or expose private evidence.
            throw new InvalidOperationException("Committed contribution retrieval is unavailable.");
        }
    }
}
