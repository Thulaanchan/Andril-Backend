using System.Text.Json.Serialization;
using Andril.Learning.Entities;

namespace Andril.Learning.Interfaces;

/// <summary>
/// PROVISIONAL Learning-owned backend contract for the two existing setup entity mappings.
/// No implementation, runtime registration, shared M2 authority or HTTP contract is supplied.
/// Interests/preferences aggregate storage is not implied by these two mapped row collections.
/// </summary>
public interface ILearningSetupRepository
{
    /// <summary>
    /// Obtain a complete consistent scope snapshot and opaque storage revision. Resolve current
    /// authenticated account/profile ownership, active lifecycle, standing and operation permission
    /// through authoritative integration, never these supplied IDs. Eligible setup operations need
    /// not require completed setup. Revalidate at coordinated release; no positive authorization TTL.
    /// Denied/missing/foreign scope must not reveal resource existence; unavailable authority must
    /// not become an available empty snapshot. Return detached private data only after validation.
    /// </summary>
    Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reconcile both collections for this exact scope, or change neither. The adapter
    /// must validate scope/rows and nonblank revisions, coordinate Account -> Module membership ->
    /// Protected resource using the shared mechanism, re-read current authority within that boundary,
    /// compare the storage revision and validate current authoritative catalogue revision/eligibility
    /// for new selections. Hold coordination through commit/rollback. No independent lock namespace.
    /// Caller revisions are comparison inputs, never authority or permission. Preserve allowed
    /// teaching/learning overlap and stable identities; never remap historical skills automatically.
    /// Removal/storage retention semantics require separate approval; this contract grants no purge.
    /// A stale storage/catalogue revision yields Conflict without partial writes or automatic retry.
    /// Committed requires a known durable commit; ambiguous commits yield CommitUnknown, not success,
    /// rollback claims or redispatch. No external effects, rewards or onboarding/access grants.
    /// </summary>
    /// <remarks>
    /// For both operations, propagate cancellation before work and during cancellable coordination;
    /// never translate it into denial, empty data or success. Roll back uncommitted changes. If commit
    /// outcome is uncertain, report CommitUnknown rather than falsely asserting cancellation rolled
    /// back a durable write. No silent retries. Unexpected programming failures propagate; anticipated
    /// authority/storage unavailability uses the safe outcomes without raw exceptions/private data.
    /// Exact production principal keys, FK/delete behavior, revision source, coordination and catalogue
    /// adapters remain pending M2/M6 integration; implementations must fail closed until supplied.
    /// </remarks>
    Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement,
        string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default);
}

/// <summary>Structural Learning-only target, never an actor, credential or authorization receipt.</summary>
public sealed class LearningSetupScope
{
    [JsonIgnore] public Guid AccountId { get; }
    [JsonIgnore] public Guid LearningProfileId { get; }
    [JsonIgnore] public string Module => "learning";

    public LearningSetupScope(Guid accountId, Guid learningProfileId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account reference is required.", nameof(accountId));
        if (learningProfileId == Guid.Empty) throw new ArgumentException("A Learning profile reference is required.", nameof(learningProfileId));
        AccountId = accountId; LearningProfileId = learningProfileId;
    }
    public override string ToString() => "Learning setup scope [private references omitted; no authority]";
}

/// <summary>Detached immutable scalar copies; construction checks structure, not ownership,
/// completeness, catalogue freshness or permission. Never use as a public DTO.</summary>
public sealed class LearningSetupSnapshot
{
    [JsonIgnore] public LearningSetupScope Scope { get; }
    [JsonIgnore] public IReadOnlyList<Selection> Selections { get; }
    [JsonIgnore] public IReadOnlyList<Goal> Goals { get; }

    public LearningSetupSnapshot(LearningSetupScope scope, IEnumerable<LearningSkillSelection> selections,
        IEnumerable<LearningPersonalGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(goals);
        var copiedSelections = new List<Selection>(); var selectionIds = new HashSet<Guid>();
        var memberships = new HashSet<(LearningSkillSelectionKind, Guid)>();
        foreach (var row in selections)
        {
            if (row is null || row.LearningProfileId != scope.LearningProfileId || row.Module != scope.Module ||
                row.Id == Guid.Empty || row.SkillId == Guid.Empty || !Enum.IsDefined(row.Kind) ||
                !selectionIds.Add(row.Id) || !memberships.Add((row.Kind, row.SkillId)))
                throw new ArgumentException("Invalid scoped selection snapshot.", nameof(selections));
            copiedSelections.Add(new(row.Id, row.SkillId, row.Kind));
        }
        var copiedGoals = new List<Goal>(); var goalIds = new HashSet<Guid>();
        foreach (var row in goals)
        {
            if (row is null || row.LearningProfileId != scope.LearningProfileId || row.Module != scope.Module ||
                row.Id == Guid.Empty || string.IsNullOrWhiteSpace(row.Text) || !goalIds.Add(row.Id))
                throw new ArgumentException("Invalid scoped goal snapshot.", nameof(goals));
            copiedGoals.Add(new(row.Id, row.Text));
        }
        Scope = scope; Selections = copiedSelections.AsReadOnly(); Goals = copiedGoals.AsReadOnly();
    }
    public override string ToString() => "Private Learning setup snapshot [rows and scope omitted]";

    public sealed class Selection
    {
        [JsonIgnore] public Guid Id { get; }
        [JsonIgnore] public Guid SkillId { get; }
        [JsonIgnore] public LearningSkillSelectionKind Kind { get; }
        internal Selection(Guid id, Guid skillId, LearningSkillSelectionKind kind) { Id = id; SkillId = skillId; Kind = kind; }
        public override string ToString() => "Private Learning selection snapshot [values omitted]";
    }
    public sealed class Goal
    {
        [JsonIgnore] public Guid Id { get; }
        [JsonIgnore] public string Text { get; }
        internal Goal(Guid id, string text) { Id = id; Text = text; }
        public override string ToString() => "Private Learning goal snapshot [values omitted]";
    }
}

public enum LearningSetupReadState { Available = 1, Denied = 2, Unavailable = 3 }
public enum LearningSetupWriteOutcome { Committed = 1, Denied = 2, Unavailable = 3, Conflict = 4, CommitUnknown = 5 }

/// <summary>Structural result only; Available construction cannot establish trusted evidence.</summary>
public sealed class LearningSetupReadResult
{
    [JsonIgnore] public LearningSetupReadState State { get; }
    [JsonIgnore] public LearningSetupSnapshot? Snapshot { get; }
    [JsonIgnore] public string? StorageRevision { get; }
    private LearningSetupReadResult(LearningSetupReadState state, LearningSetupSnapshot? snapshot, string? revision)
    { State = state; Snapshot = snapshot; StorageRevision = revision; }
    public static LearningSetupReadResult Available(LearningSetupSnapshot snapshot, string storageRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(storageRevision)) throw new ArgumentException("Storage revision is required.", nameof(storageRevision));
        return new(LearningSetupReadState.Available, snapshot, storageRevision);
    }
    public static LearningSetupReadResult Denied() => new(LearningSetupReadState.Denied, null, null);
    public static LearningSetupReadResult Unavailable() => new(LearningSetupReadState.Unavailable, null, null);
    public override string ToString() => "Learning setup read result [private evidence omitted]";
}
