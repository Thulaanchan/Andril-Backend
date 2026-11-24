using System.Data;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
namespace Andril.Persistence.Repositories.Hobbies;

/// <summary>Unregistered adapter for the accepted lifecycle schema. Never provisions or repairs it.</summary>
/// <remarks>The factory must create a fresh independently owned context with Enlist=false.
/// No runtime permission provider, migrations, submission coordination or DI activation is supplied.</remarks>
internal sealed class MapAttractionLifecycleWriter(IDbContextFactory<HobbiesMapDbContext> contexts,
    IMapAttractionManagementAuthorization authorization, ICurrentUser user) : IMapAttractionLifecycleWriter
{
    private readonly IDbContextFactory<HobbiesMapDbContext> _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    private readonly IMapAttractionManagementAuthorization _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    private readonly ICurrentUser _user = user ?? throw new ArgumentNullException(nameof(user));

    public async Task<MapAttractionLifecycleResult> ChangeAsync(MapAttractionLifecycleChange change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(change);
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Lifecycle writes do not accept ambient transactions.");
        var committing = false;
        try
        {
            if (!_user.IsAuthenticated || _user.UserId is not Guid subject || subject == Guid.Empty)
                return new(MapAttractionLifecycleOutcome.Denied);
            await using var context = await _contexts.CreateDbContextAsync(cancellationToken);
            var connection = context.Database.GetDbConnection() as NpgsqlConnection;
            if (connection is null || connection.State != ConnectionState.Closed || context.Database.CurrentTransaction is not null
                || context.ChangeTracker.HasChanges()) return new(MapAttractionLifecycleOutcome.Unavailable);
            var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            if (settings.Enlist || settings.Multiplexing || settings.NoResetOnClose)
                return new(MapAttractionLifecycleOutcome.Unavailable);
            await context.Database.OpenConnectionAsync(cancellationToken);
            await using var owned = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
            var transaction = (NpgsqlTransaction)owned.GetDbTransaction();
            var decision = await _authorization.AuthorizeAsync(connection,transaction,change.AttractionId,change.IsActive,cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_user.IsAuthenticated || _user.UserId != subject) return new(MapAttractionLifecycleOutcome.Denied);
            if (decision?.Outcome == MapAttractionManagementAuthorizationOutcome.Denied) return new(MapAttractionLifecycleOutcome.Denied);
            if (decision is not { Outcome: MapAttractionManagementAuthorizationOutcome.Satisfied, Grant: { } grant }
                || grant.ReviewerSubjectId != subject || grant.AttractionId != change.AttractionId || grant.RequestedIsActive != change.IsActive)
                return new(MapAttractionLifecycleOutcome.Unavailable);
            MapAttractionStatePrototype state;
            long sequence;
            await using (var read = Command(connection,transaction,"""
                SELECT s.is_active,s.version,s.operation_sequence,a.name,a.latitude,a.longitude
                FROM andril_hobbies.map_attraction_lifecycle s JOIN andril_hobbies.map_attractions a USING(attraction_id)
                WHERE s.attraction_id=$1 FOR UPDATE OF s
                """,change.AttractionId))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return new(MapAttractionLifecycleOutcome.Denied);
                state = MapAttractionStatePrototype.CreateSnapshot(MapAttraction.Create(change.AttractionId,reader.GetString(3),reader.GetDouble(4),reader.GetDouble(5)),reader.GetBoolean(0),reader.GetInt64(1));
                sequence = reader.GetInt64(2);
            }
            await using (var prior = Command(connection,transaction,"""
                SELECT r.attraction_id,r.requested_is_active,r.expected_version,r.result_version,
                    ROW(r.receipt_id,r.reviewer_subject_id,r.request_id,r.attraction_id,r.requested_is_active,r.expected_version,
                        r.previous_is_active,r.previous_version,r.result_is_active,r.result_version,r.previous_operation_sequence,r.result_operation_sequence,r.outcome,r.accepted_at_utc)
                    IS NOT DISTINCT FROM ROW(a.receipt_id,a.reviewer_subject_id,a.request_id,a.attraction_id,a.requested_is_active,a.expected_version,
                        a.previous_is_active,a.previous_version,a.result_is_active,a.result_version,a.previous_operation_sequence,a.result_operation_sequence,a.outcome,a.accepted_at_utc)
                FROM andril_hobbies.map_attraction_lifecycle_receipts r
                LEFT JOIN andril_hobbies.map_attraction_lifecycle_audit a USING(receipt_id)
                WHERE r.reviewer_subject_id=$1 AND r.request_id=$2
                """,subject,change.RequestId))
            await using (var reader = await prior.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.GetBoolean(4)) return new(MapAttractionLifecycleOutcome.Unavailable);
                    if (reader.GetGuid(0) != change.AttractionId || reader.GetBoolean(1) != change.IsActive || reader.GetInt64(2) != change.ExpectedVersion)
                        return new(MapAttractionLifecycleOutcome.Conflict);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(MapAttractionLifecycleOutcome.ExistingAuthorized,change.RequestId,change.AttractionId,change.IsActive,reader.GetInt64(3));
                }
            }
            if (state.Version != change.ExpectedVersion || sequence == long.MaxValue) return new(MapAttractionLifecycleOutcome.Conflict);
            MapAttractionStatePrototype next;
            try { next = state.Transition(change.IsActive,change.ExpectedVersion); }
            catch (OverflowException) { return new(MapAttractionLifecycleOutcome.Conflict); }
            var outcome = ReferenceEquals(state,next) ? MapAttractionLifecycleOutcome.Unchanged : MapAttractionLifecycleOutcome.Changed;
            var receiptId = Guid.NewGuid();
            await using (var insert = Command(connection,transaction,"""
                INSERT INTO andril_hobbies.map_attraction_lifecycle_receipts
                (receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,
                 previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
                VALUES($1,$2,$3,$4,$5,$6,$7,$6,$5,$8,$9,$10,$11,clock_timestamp())
                """,receiptId,subject,change.RequestId,change.AttractionId,change.IsActive,change.ExpectedVersion,state.IsActive,next.Version,sequence,checked(sequence+1),(short)outcome))
                await insert.ExecuteNonQueryAsync(cancellationToken);
            await using (var audit = Command(connection,transaction,"""
                INSERT INTO andril_hobbies.map_attraction_lifecycle_audit
                (receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,
                 previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
                SELECT receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,
                    previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc
                FROM andril_hobbies.map_attraction_lifecycle_receipts WHERE receipt_id=$1
                """,receiptId)) await audit.ExecuteNonQueryAsync(cancellationToken);
            await using (var update = Command(connection,transaction,"""
                UPDATE andril_hobbies.map_attraction_lifecycle
                SET is_active=$1,version=$2,operation_sequence=$3,last_receipt_id=$4
                WHERE attraction_id=$5 AND version=$6 AND operation_sequence=$7
                """,next.IsActive,next.Version,sequence+1,receiptId,change.AttractionId,state.Version,sequence))
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return new(MapAttractionLifecycleOutcome.Conflict);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_user.IsAuthenticated || _user.UserId != subject) return new(MapAttractionLifecycleOutcome.Denied);
            committing = true;
            await owned.CommitAsync(cancellationToken);
            // Acknowledged commit must not be replaced with a late cancellation result.
            return new(outcome,change.RequestId,change.AttractionId,next.IsActive,next.Version);
        }
        catch (OperationCanceledException) when (!committing) { throw; }
        catch (Exception)
        {
            if (!committing) cancellationToken.ThrowIfCancellationRequested();
            return new(committing ? MapAttractionLifecycleOutcome.OutcomeUnknown : MapAttractionLifecycleOutcome.Unavailable);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection c,NpgsqlTransaction t,string sql,params object[] values)
    { var cmd=new NpgsqlCommand(sql,c,t); foreach(var value in values) cmd.Parameters.AddWithValue(value); return cmd; }
}
