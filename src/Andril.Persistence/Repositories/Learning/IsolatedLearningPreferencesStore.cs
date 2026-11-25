using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>
/// ISOLATED, UNREGISTERED data component, not ILearningSetupRepository or an authorization
/// adapter. The caller owns the connection/transaction, authorization fences, current catalogue,
/// read release and commit/rollback/CommitUnknown handling. No production caller is supplied.
/// Supplied scope/catalogue/revision values cannot authorize access. Do not wire into runtime DI.
/// This component does not solve the pending shared M2 account/membership coordination contract.
/// </summary>
internal sealed class IsolatedLearningPreferencesStore(LearningDbContext db)
{
    internal enum ReplacementState { Prepared, Conflict }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("An externally owned transaction and clean context are required.");
        if (db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("The isolated store requires Serializable isolation.");
    }

    internal async Task<LearningSetupReadResult> ReadUnderTransactionAsync(LearningSetupScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested(); RequireTransaction();
        // PostgreSQL resource locks only, deliberately conservative/table-wide in this isolated
        // prototype. Not an independent M2 lock namespace or a production revocation fence.
        // Lock selections first; Serializable provides one MVCC snapshot for both collections
        // and SSI conflict detection. Goal SELECT uses only ACCESS SHARE/predicate locks and
        // needs no goal UPDATE/DELETE/MAINTAIN privilege. Concurrent goal writers must also use
        // Serializable for SSI guarantees. A valid concurrent goal write can serialize after
        // this transaction; this is not a promise of latest-at-commit goal values or M2 fencing.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE andril_learning.learning_skill_selections IN SHARE ROW EXCLUSIVE MODE", ct);
        var selections = await db.SkillSelections.AsNoTracking().Where(s => s.LearningProfileId == scope.LearningProfileId && s.Module == scope.Module)
            .OrderBy(s => s.Id).ToArrayAsync(ct);
        var goals = await db.PersonalGoals.AsNoTracking().Where(g => g.LearningProfileId == scope.LearningProfileId && g.Module == scope.Module)
            .OrderBy(g => g.Id).ToArrayAsync(ct);
        // Global MVCC snapshot is intentionally conservative: unrelated committed writes can
        // invalidate a revision. Includes empty scopes/ABA, not a new durable revision schema.
        // This is not the still-pending production scoped revision source or an access receipt.
        var snapshot = await db.Database.SqlQueryRaw<string>("SELECT txid_current_snapshot()::text AS \"Value\"").SingleAsync(ct);
        var serialized = JsonSerializer.Serialize(new
        {
            scope.AccountId, scope.LearningProfileId, scope.Module, Snapshot = snapshot,
            Selections = selections.Select(s => new { s.Id, s.SkillId, s.Kind }),
            Goals = goals.Select(g => new { g.Id, g.Text })
        });
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        ct.ThrowIfCancellationRequested();
        return LearningSetupReadResult.Available(new(scope, selections, goals), revision);
    }

    /// <summary>Only prepares database changes in the caller transaction; never reports a
    /// durable commit. Caller must rollback on cancellation/failure/conflict and freshly
    /// coordinate authorization/catalogue before commit. Goal editing/purging is unsupported.</summary>
    internal async Task<ReplacementState> PrepareReplacementAsync(LearningSetupSnapshot replacement,
        string expectedRevision, LearningSkillCatalogue catalogue, CancellationToken ct = default)
    {
        try { return await PrepareCoreAsync(replacement, expectedRevision, catalogue, ct); }
        catch (Exception error) when (IsConflict(error))
        {
            // PostgreSQL has aborted this transaction. Caller must dispose/rollback it; do
            // not reuse the tracked context or retry. No partial changes may be committed.
            return ReplacementState.Conflict;
        }
    }

    private async Task<ReplacementState> PrepareCoreAsync(LearningSetupSnapshot replacement,
        string expectedRevision, LearningSkillCatalogue catalogue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replacement); ArgumentNullException.ThrowIfNull(catalogue);
        var current = await ReadUnderTransactionAsync(replacement.Scope, ct);
        if (string.IsNullOrWhiteSpace(expectedRevision) || current.StorageRevision != expectedRevision)
            return ReplacementState.Conflict;
        var old = current.Snapshot!;
        if (!old.Goals.Select(g => (g.Id, g.Text)).OrderBy(g => g.Id)
            .SequenceEqual(replacement.Goals.Select(g => (g.Id, g.Text)).OrderBy(g => g.Id)))
            return ReplacementState.Conflict;
        var identities = old.Selections.ToDictionary(s => (s.Kind, s.SkillId), s => s.Id);
        if (replacement.Selections.Any(s => identities.TryGetValue((s.Kind, s.SkillId), out var id) && id != s.Id)
            || replacement.Selections.Any(s => old.Selections.Any(o => o.Id == s.Id && (o.Kind != s.Kind || o.SkillId != s.SkillId))))
            return ReplacementState.Conflict;
        var preferences = new LearningPreferences(replacement.Scope.AccountId, replacement.Scope.LearningProfileId);
        preferences.UpdateSelections([], replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId),
            replacement.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Learning).Select(s => s.SkillId), catalogue);
        var retained = replacement.Selections.Select(s => s.Id).ToHashSet();
        var removed = await db.SkillSelections.Where(s => s.LearningProfileId == replacement.Scope.LearningProfileId
            && s.Module == replacement.Scope.Module && !retained.Contains(s.Id)).ToArrayAsync(ct);
        db.SkillSelections.RemoveRange(removed);
        var existing = old.Selections.Select(s => s.Id).ToHashSet();
        foreach (var row in replacement.Selections.Where(s => !existing.Contains(s.Id)))
            db.SkillSelections.Add(LearningSkillSelection.CreateFromPreferences(row.Id, preferences, row.SkillId, row.Kind));
        await db.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        return ReplacementState.Prepared;
    }

    private static bool IsConflict(Exception error) => error is PostgresException
        { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName:
                "uq_learning_skill_selections_profile_purpose_skill" or "pk_learning_skill_selections" } }
        // Verified Npgsql non-retrying execution strategy wrapper from the real SSI test.
        // Match the precise exception chain and SQLSTATE; never infer conflict from the
        // generic outer "transient failure" message or arbitrary nested database errors.
        || error is InvalidOperationException { InnerException: DbUpdateException
            { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } } };

    /// <summary>Optional caller-invoked commit boundary AFTER its coordinated revalidation.
    /// No authorization is performed here. Serializable may fail at commit, not only SaveChanges.
    /// Caller still owns disposal/rollback; no retry. Unknown delivery is never called success.</summary>
    internal async Task<LearningSetupWriteOutcome> CommitPreparedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); RequireTransaction();
        try
        {
            await db.Database.CurrentTransaction!.CommitAsync(ct);
            return LearningSetupWriteOutcome.Committed;
        }
        catch (Exception error) when (IsConflict(error)) { return LearningSetupWriteOutcome.Conflict; }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException or TimeoutException or OperationCanceledException)
        { return LearningSetupWriteOutcome.CommitUnknown; }
    }
}
