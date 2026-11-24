namespace Andril.Learning.Circles;

/// <summary>
/// DEC-20261120-LRN02-01: in-memory Learning Circle and shared-goal invariants only.
/// This object is not an authenticated resource, M5 group, Classroom, membership or access grant.
/// Future protected callers must verify fresh M2 Learning eligibility/privacy and authoritative
/// M5 membership/role permission before loading, viewing or mutating private Circle content.
/// Neither this identity nor possession of the object proves permission. Do not expose or serialize
/// the domain instance through an API. There is no repository, registration or access implementation.
/// DEC-M1-02 adds local manually reported progress only, never verified achievement or rewards.
/// Authoritative attribution, goal deletion, Circle lifecycle and concurrency remain pending.
/// </summary>
public sealed class LearningCircle
{
    public Guid Id { get; }
    public string Module => "learning";
    public IReadOnlyList<SharedGoal> Goals { get; private set; } = Array.AsReadOnly(Array.Empty<SharedGoal>());

    public LearningCircle(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A Learning Circle ID is required.", nameof(id));
        Id = id;
    }

    /// <summary>Construct a shared goal after external permission checks. Text is retained exactly;
    /// no title/description schema, length limit, category or deadline is chosen. Reported progress starts at zero.</summary>
    public SharedGoal CreateSharedGoal(string text)
    {
        RequireText(text);
        var goal = new SharedGoal(Guid.NewGuid(), Id, text);
        var next = Goals.Append(goal).ToArray();
        Goals = Array.AsReadOnly(next);
        return goal;
    }

    /// <summary>Edit only an existing local goal, preserving identity and unrelated goals.
    /// This structural scope check is not ownership, membership or role authorization.</summary>
    public SharedGoal EditSharedGoal(Guid goalId, string text)
    {
        if (goalId == Guid.Empty) throw new ArgumentException("A shared goal ID is required.", nameof(goalId));
        RequireText(text);
        var index = FindGoal(goalId);

        var replacement = new SharedGoal(goalId, Id, text, Goals[index].ProgressPercent);
        var next = Goals.ToArray();
        next[index] = replacement;
        Goals = Array.AsReadOnly(next);
        return replacement;
    }

    /// <summary>
    /// DEC-M1-02: explicitly replace reported progress with any integer from 0 through 100,
    /// including decreases. A value of 100 is self-reported completion, not verified achievement.
    /// Preserve text, identity, unrelated goals and previous immutable snapshots. No XP or reward.
    /// This local lookup accepts no actor claims; protected callers must freshly authorize editing.
    /// </summary>
    public SharedGoal UpdateSharedGoalProgress(Guid goalId, int progressPercent)
    {
        var index = FindGoal(goalId);
        var replacement = new SharedGoal(goalId, Id, Goals[index].Text, progressPercent);
        var next = Goals.ToArray();
        next[index] = replacement;
        Goals = Array.AsReadOnly(next);
        return replacement;
    }

    private int FindGoal(Guid goalId)
    {
        if (goalId == Guid.Empty) throw new ArgumentException("A shared goal ID is required.", nameof(goalId));
        for (var i = 0; i < Goals.Count; i++)
            if (Goals[i].Id == goalId) return i;
        throw new KeyNotFoundException("Shared goal is not present in this domain instance.");
    }

    private static void RequireText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Shared goal text is required.", nameof(text));
    }

    public override string ToString() => "Learning Circle [private identity and goals omitted]";

    /// <summary>Immutable goal snapshot scoped to one Circle. Not a public content projection,
    /// authorization receipt or verification record. Progress is manually reported only.</summary>
    public sealed class SharedGoal
    {
        public Guid Id { get; }
        public Guid CircleId { get; }
        public string Text { get; }
        public int ProgressPercent { get; }

        internal SharedGoal(Guid id, Guid circleId, string text, int progressPercent = 0)
        {
            if (progressPercent is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(progressPercent), "Reported progress must be from 0 through 100.");
            Id = id; CircleId = circleId; Text = text; ProgressPercent = progressPercent;
        }

        public override string ToString() => "Learning shared goal [private identity and text omitted]";
    }
}
