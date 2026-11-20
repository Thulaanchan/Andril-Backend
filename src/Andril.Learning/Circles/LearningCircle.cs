namespace Andril.Learning.Circles;

/// <summary>
/// DEC-20261120-LRN02-01: in-memory Learning Circle and shared-goal invariants only.
/// This object is not an authenticated resource, M5 group, Classroom, membership or access grant.
/// Future protected callers must verify fresh M2 Learning eligibility/privacy and authoritative
/// M5 membership/role permission before loading, viewing or mutating private Circle content.
/// Neither this identity nor possession of the object proves permission. Do not expose or serialize
/// the domain instance through an API. There is no repository, registration or access implementation.
/// Progress measurement/attribution, goal deletion, Circle lifecycle and concurrency remain pending.
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
    /// no title/description schema, length limit, category, deadline or completion rule is chosen.</summary>
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
        var index = -1;
        for (var i = 0; i < Goals.Count; i++)
            if (Goals[i].Id == goalId) { index = i; break; }
        if (index < 0) throw new KeyNotFoundException("Shared goal is not present in this domain instance.");

        var replacement = new SharedGoal(goalId, Id, text);
        var next = Goals.ToArray();
        next[index] = replacement;
        Goals = Array.AsReadOnly(next);
        return replacement;
    }

    private static void RequireText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Shared goal text is required.", nameof(text));
    }

    public override string ToString() => "Learning Circle [private identity and goals omitted]";

    /// <summary>Immutable goal snapshot scoped to one Circle. Not a public content projection,
    /// authorization receipt or progress record. No role/visibility/progress claims are supplied.</summary>
    public sealed class SharedGoal
    {
        public Guid Id { get; }
        public Guid CircleId { get; }
        public string Text { get; }

        internal SharedGoal(Guid id, Guid circleId, string text) { Id = id; CircleId = circleId; Text = text; }

        public override string ToString() => "Learning shared goal [private identity and text omitted]";
    }
}
