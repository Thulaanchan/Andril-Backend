using Andril.Learning.Entities;
using Andril.Learning.Interfaces;

namespace Andril.Learning.Services;

public sealed record LearningHomeSkill(Guid Id, Guid SkillId, LearningSkillSelectionKind Kind);
public sealed record LearningHomeGoal(Guid Id, string Text)
{
    public override string ToString() => "Private Learning Home goal [content omitted]";
}
public sealed record LearningHomeView(string Revision, IReadOnlyList<LearningHomeSkill> Skills, IReadOnlyList<LearningHomeGoal> Goals)
{
    public override string ToString() => "Private Learning Home [content omitted]";
}
public sealed record LearningHomeResult(LearningSetupReadState State, LearningHomeView? Home = null);

/// <summary>
/// Owner-only Home projection of actual persisted preferences/goals. Reuses the existing
/// current authorization, revision and coordinated repository read-release boundary.
/// No invented ranking, feed, XP, unread counters, partner or Classroom permissions.
/// </summary>
public sealed class LearningHomeApplicationService(LearningPreferencesApplicationService preferences, ILearningSetupAccessBoundary access)
{
    public async Task<LearningHomeResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var permission = await access.CheckAsync(LearningSetupOperation.EnterHome, cancellationToken);
        if (permission.State != LearningSetupAccessState.Allowed || permission.Scope is null)
            return new(permission.State == LearningSetupAccessState.Denied ? LearningSetupReadState.Denied : LearningSetupReadState.Unavailable);
        var read = await preferences.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (read.State != LearningSetupReadState.Available) return new(read.State);
        if (read.Snapshot is null || string.IsNullOrWhiteSpace(read.StorageRevision)) return new(LearningSetupReadState.Unavailable);
        var fresh = await access.CheckAsync(LearningSetupOperation.EnterHome, cancellationToken);
        if (fresh.State != LearningSetupAccessState.Allowed || fresh.Scope?.AccountId != permission.Scope.AccountId ||
            fresh.Scope.LearningProfileId != permission.Scope.LearningProfileId || read.Snapshot.Scope.AccountId != permission.Scope.AccountId ||
            read.Snapshot.Scope.LearningProfileId != permission.Scope.LearningProfileId)
            return new(fresh.State == LearningSetupAccessState.Unavailable ? LearningSetupReadState.Unavailable : LearningSetupReadState.Denied);
        return new(LearningSetupReadState.Available, new(read.StorageRevision,
            Array.AsReadOnly(read.Snapshot.Selections.Select(x => new LearningHomeSkill(x.Id, x.SkillId, x.Kind)).ToArray()),
            Array.AsReadOnly(read.Snapshot.Goals.Select(x => new LearningHomeGoal(x.Id, x.Text)).ToArray())));
    }
}
