using System.Data;
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
    internal enum ReplacementState { Prepared, Conflict, Unavailable }

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
        // Point lookups avoid relation-wide SSI predicate locks for empty profile collections.
        // These are local plan choices, not weaker isolation or automatic conflict retries.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
        // Serializable gives the revision and both collections the same MVCC snapshot.
        // Reads do not take writer locks; the coordinated adapter owns authorization fencing.
        var revision = await db.Set<LearningProfileRevisionRow>().AsNoTracking().SingleOrDefaultAsync(
            r => r.LearningProfileId == scope.LearningProfileId && r.Module == scope.Module && r.AccountId == scope.AccountId, ct);
        if (revision is null) return LearningSetupReadResult.Unavailable();
        LearningSkillSelection[] selections = revision.SelectionIds.Length == 0 ? [] : await db.SkillSelections.AsNoTracking()
            .Where(s => revision.SelectionIds.Contains(s.Id) && s.LearningProfileId == scope.LearningProfileId && s.Module == scope.Module)
            .OrderBy(s => s.Id).ToArrayAsync(ct);
        LearningPersonalGoal[] goals = revision.GoalIds.Length == 0 ? [] : await db.PersonalGoals.AsNoTracking()
            .Where(g => revision.GoalIds.Contains(g.Id) && g.LearningProfileId == scope.LearningProfileId && g.Module == scope.Module)
            .OrderBy(g => g.Id).ToArrayAsync(ct);
        if (selections.Length != revision.SelectionIds.Length || goals.Length != revision.GoalIds.Length)
            return LearningSetupReadResult.Unavailable();
        ct.ThrowIfCancellationRequested();
        return LearningSetupReadResult.Available(new(scope, selections, goals),
            LearningSetupRevision.FromPersisted(revision.GenerationId, revision.Version).ToOpaqueToken());
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
        catch (PostgresException error) when (error.SqlState is "P2001" or "P2002")
        { return ReplacementState.Unavailable; }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "P2001" or "P2002" })
        { return ReplacementState.Unavailable; }
    }

    private async Task<ReplacementState> PrepareCoreAsync(LearningSetupSnapshot replacement,
        string expectedRevision, LearningSkillCatalogue catalogue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replacement); ArgumentNullException.ThrowIfNull(catalogue);
        ct.ThrowIfCancellationRequested(); RequireTransaction();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
        if (!LearningSetupRevision.TryParse(expectedRevision, out _)) return ReplacementState.Conflict;
        // Profile row, not a table-wide lock. SELECT-only goal permissions remain sufficient.
        // FOR NO KEY UPDATE avoids DELETE/UPDATE privileges on the goals table.
        // The dedicated security-definer function exposes only this lock, not revision writes.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT andril_learning.lock_profile_revision({replacement.Scope.LearningProfileId}, {replacement.Scope.AccountId})", ct);
        var current = await ReadUnderTransactionAsync(replacement.Scope, ct);
        if (current.State != LearningSetupReadState.Available) return ReplacementState.Unavailable;
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
        var removedIds = old.Selections.Where(s => !retained.Contains(s.Id)).Select(s => s.Id).ToArray();
        LearningSkillSelection[] removed = removedIds.Length == 0 ? [] : await db.SkillSelections.Where(s => removedIds.Contains(s.Id)
            && s.LearningProfileId == replacement.Scope.LearningProfileId && s.Module == replacement.Scope.Module).ToArrayAsync(ct);
        db.SkillSelections.RemoveRange(removed);
        var existing = old.Selections.Select(s => s.Id).ToHashSet();
        foreach (var row in replacement.Selections.Where(s => !existing.Contains(s.Id)))
            db.SkillSelections.Add(LearningSkillSelection.CreateFromPreferences(row.Id, preferences, row.SkillId, row.Kind));
        await db.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        return ReplacementState.Prepared;
    }

    internal static bool IsConflict(Exception error) => error is PostgresException
        { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName:
                "uq_learning_skill_selections_profile_purpose_skill" or "pk_learning_skill_selections" or "pk_learning_mutation_receipts" } }
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
