using Andril.Learning.Entities;

namespace Andril.Learning.Interfaces;

/// <summary>Learning-owned controlled command extension, never a shared M2 authority.
/// Operation IDs identify intent/reconciliation only, never ownership or permission. No replay.
/// Goal removal implements the existing domain removal operation, not account-retention policy.</summary>
public interface ILearningSetupMutationRepository : ILearningSetupRepository
{
    Task<LearningSetupWriteOutcome> ReplaceWithOperationAsync(Guid operationId, LearningSetupSnapshot replacement,
        string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default);
    Task<LearningSetupWriteOutcome> ChangeGoalAsync(Guid operationId, LearningSetupScope scope,
        LearningGoalChange change, string expectedRevision, CancellationToken cancellationToken = default);
}

public enum LearningGoalChangeKind { Create = 1, Edit = 2, Remove = 3 }

public sealed class LearningGoalChange
{
    public LearningGoalChangeKind Kind { get; }
    public Guid GoalId { get; }
    public string? Text { get; }
    private LearningGoalChange(LearningGoalChangeKind kind, Guid goalId, string? text)
    { Kind = kind; GoalId = goalId; Text = text; }

    public static LearningGoalChange Create(string text) => new(LearningGoalChangeKind.Create, Guid.NewGuid(), Validate(text));
    public static LearningGoalChange Edit(Guid id, string text) => new(LearningGoalChangeKind.Edit, RequireId(id), Validate(text));
    public static LearningGoalChange Remove(Guid id) => new(LearningGoalChangeKind.Remove, RequireId(id), null);
    private static Guid RequireId(Guid id) => id != Guid.Empty ? id : throw new ArgumentException("A goal reference is required.");
    private static string Validate(string text)
    {
        // Reuse the existing domain rule; no new text length, normalization or retention rule.
        var preferences = new LearningPreferences(Guid.NewGuid(), Guid.NewGuid());
        return preferences.CreateGoal(text).Text;
    }
    public override string ToString() => "Private Learning goal change [values omitted]";
}
