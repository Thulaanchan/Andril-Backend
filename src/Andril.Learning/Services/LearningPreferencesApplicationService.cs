using Andril.Learning.Entities;
using Andril.Learning.Interfaces;

namespace Andril.Learning.Services;

/// <summary>
/// Unregistered provisional orchestration for mapped skill selections and goal reads only.
/// Interests and goal editing are outside this storage slice. No production authority/provider.
/// Preflight checks do not replace ILearningSetupRepository's coordinated authorization at
/// read release and transaction commit, including current catalogue revision verification.
/// No positive cache, retry, external effects, onboarding grant or claimed database atomicity.
/// </summary>
public sealed class LearningPreferencesApplicationService(
    ILearningSetupAccessBoundary access, ILearningSetupRepository repository, ILearningSkillCatalogueReader catalogue)
{
    public async Task<LearningSetupReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = await access.CheckAsync(LearningSetupOperation.ReadSetup, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (authorization.State == LearningSetupAccessState.Denied) return LearningSetupReadResult.Denied();
        if (authorization.State != LearningSetupAccessState.Allowed || authorization.Scope is null)
            return LearningSetupReadResult.Unavailable();
        var result = await repository.ReadAsync(authorization.Scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.State != LearningSetupReadState.Available) return result;
        if (result.Snapshot is null || !SameScope(authorization.Scope, result.Snapshot.Scope)) return LearningSetupReadResult.Denied();
        var fresh = await access.CheckAsync(LearningSetupOperation.ReadSetup, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fresh.State == LearningSetupAccessState.Denied ||
            (fresh.Scope is not null && !SameScope(authorization.Scope, fresh.Scope))) return LearningSetupReadResult.Denied();
        return fresh.State == LearningSetupAccessState.Allowed && fresh.Scope is not null
            ? result : LearningSetupReadResult.Unavailable();
    }

    /// <summary>Replace the two skill lists after domain/catalogue validation. Invalid input
    /// propagates existing validation exceptions before ReplaceAsync; existing snapshots are
    /// never mutated. Repository outcomes, including Conflict/CommitUnknown, are not retried.
    /// Never check cancellation after a known commit and falsely imply it rolled back.</summary>
    public async Task<LearningSetupWriteOutcome> UpdateSelectionsAsync(IEnumerable<Guid> teachingSkills,
        IEnumerable<Guid> learningSkills, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = await access.CheckAsync(LearningSetupOperation.UpdateSetup, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (authorization.State == LearningSetupAccessState.Denied) return LearningSetupWriteOutcome.Denied;
        if (authorization.State != LearningSetupAccessState.Allowed || authorization.Scope is null) return LearningSetupWriteOutcome.Unavailable;
        var current = await repository.ReadAsync(authorization.Scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (current.State == LearningSetupReadState.Denied) return LearningSetupWriteOutcome.Denied;
        if (current.State != LearningSetupReadState.Available || current.Snapshot is null || string.IsNullOrWhiteSpace(current.StorageRevision))
            return LearningSetupWriteOutcome.Unavailable;
        if (!SameScope(authorization.Scope, current.Snapshot.Scope)) return LearningSetupWriteOutcome.Denied;
        var evidence = await catalogue.ReadCurrentAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.State != LearningSkillCatalogueReadState.Available || evidence.Catalogue is null || string.IsNullOrWhiteSpace(evidence.Revision))
            return LearningSetupWriteOutcome.Unavailable;

        var preferences = new LearningPreferences(authorization.Scope.AccountId, authorization.Scope.LearningProfileId);
        preferences.UpdateSelections([], teachingSkills, learningSkills, evidence.Catalogue);
        var oldIds = current.Snapshot.Selections.ToDictionary(row => (row.Kind, row.SkillId), row => row.Id);
        var rows = preferences.TeachingSkills.Select(id => Row(id, LearningSkillSelectionKind.Teaching))
            .Concat(preferences.LearningSkills.Select(id => Row(id, LearningSkillSelectionKind.Learning))).ToArray();
        var replacement = current.Snapshot.WithSelections(rows);
        var fresh = await access.CheckAsync(LearningSetupOperation.UpdateSetup, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fresh.State == LearningSetupAccessState.Denied ||
            (fresh.Scope is not null && !SameScope(authorization.Scope, fresh.Scope))) return LearningSetupWriteOutcome.Denied;
        if (fresh.State != LearningSetupAccessState.Allowed || fresh.Scope is null) return LearningSetupWriteOutcome.Unavailable;
        return await repository.ReplaceAsync(replacement, current.StorageRevision, evidence.Revision, cancellationToken);

        LearningSkillSelection Row(Guid id, LearningSkillSelectionKind kind) => LearningSkillSelection.CreateFromPreferences(
            oldIds.TryGetValue((kind, id), out var existingId) ? existingId : Guid.NewGuid(), preferences, id, kind);
    }

    private static bool SameScope(LearningSetupScope left, LearningSetupScope right) =>
        left.AccountId == right.AccountId && left.LearningProfileId == right.LearningProfileId && left.Module == right.Module;
}
