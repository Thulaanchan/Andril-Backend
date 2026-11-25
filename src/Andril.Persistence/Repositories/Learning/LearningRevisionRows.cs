namespace Andril.Persistence.Repositories.Learning;

// Read-only projection of the verified M2 key, excluded from Learning migrations.
internal sealed class LearningProfileKeyRow
{
    public Guid Id { get; private set; }
    public string Module { get; private set; } = null!;
}

// Private persistence metadata, never HTTP DTOs or authorization evidence.
internal sealed class LearningProfileRevisionRow
{
    public Guid LearningProfileId { get; private set; }
    public string Module { get; private set; } = "learning";
    public Guid AccountId { get; private set; }
    public Guid GenerationId { get; private set; }
    public long Version { get; private set; }
    public string? LastMutationTransaction { get; private set; }
    public Guid[] SelectionIds { get; private set; } = [];
    public Guid[] GoalIds { get; private set; } = [];
}

internal sealed class LearningGenerationRow
{
    public Guid GenerationId { get; private set; }
}

internal sealed class LearningMutationReceiptRow
{
    public Guid OperationId { get; private set; }
    public Guid LearningProfileId { get; private set; }
    public string Module { get; private set; } = "learning";
    public Guid GenerationId { get; private set; }
    public long ExpectedVersion { get; private set; }
    public long ResultVersion { get; private set; }
    public string IntentHash { get; private set; } = null!;
}
