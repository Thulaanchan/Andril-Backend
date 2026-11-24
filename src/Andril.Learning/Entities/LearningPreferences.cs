namespace Andril.Learning.Entities;

/// <summary>
/// In-memory Learning domain state, not a persisted profile or authorization evidence.
/// AccountId is the internal account identity; neither supplied ID establishes ownership.
/// M2 must authorize every future load/mutation and enforce isolation at persistence boundaries.
/// No profile completion, access, XP, connections, sharing or other-module data is involved.
/// This object provides no persistence or concurrent-mutation guarantees.
/// </summary>
public sealed class LearningPreferences
{
    public Guid AccountId { get; }
    public Guid LearningProfileId { get; }
    public string Module => "learning";

    // Opaque selection identities, not a catalogue, display text or proof of catalogue validity.
    // Text comparison/normalization, catalogue values and collection limits remain undecided.
    public IReadOnlyList<Guid> Interests { get; private set; } = Array.AsReadOnly(Array.Empty<Guid>());
    public IReadOnlyList<Guid> TeachingSkills { get; private set; } = Array.AsReadOnly(Array.Empty<Guid>());
    public IReadOnlyList<Guid> LearningSkills { get; private set; } = Array.AsReadOnly(Array.Empty<Guid>());
    public IReadOnlyList<PersonalGoal> Goals { get; private set; } = Array.AsReadOnly(Array.Empty<PersonalGoal>());

    public LearningPreferences(Guid accountId, Guid learningProfileId)
    {
        RequireId(accountId, nameof(accountId));
        RequireId(learningProfileId, nameof(learningProfileId));
        AccountId = accountId;
        LearningProfileId = learningProfileId;
    }

    /// <summary>
    /// Replace selections together after complete validation; rejected updates preserve all state.
    /// Repetition means the same opaque identity in a single collection, not text equivalence.
    /// ANDRIL-M5-LRN-PRODUCT-20261122-01 permits the same skill in Teaching and Learning.
    /// Each collection still requires distinct nonempty identities and remains independent.
    /// Interest and skill identities are separate domains; Skill Exchange terms are unaffected.
    /// </summary>
    public void UpdateSelections(IEnumerable<Guid> interests, IEnumerable<Guid> teachingSkills,
        IEnumerable<Guid> learningSkills)
    {
        var nextInterests = Snapshot(interests, nameof(interests));
        var nextTeaching = Snapshot(teachingSkills, nameof(teachingSkills));
        var nextLearning = Snapshot(learningSkills, nameof(learningSkills));

        Interests = nextInterests;
        TeachingSkills = nextTeaching;
        LearningSkills = nextLearning;
    }

    /// <summary>
    /// Validate proposed replacement skill lists against a supplied offline catalogue snapshot,
    /// then replace all selections together. Snapshot caller sequences once so validation and
    /// replacement use the same values. Interests remain independent of the skill catalogue.
    /// This explicit operation does not automatically revalidate or remap historical selections.
    /// A supplied snapshot proves neither catalogue authority/freshness nor profile ownership,
    /// completion or production access; future protected callers must establish those separately.
    /// </summary>
    public void UpdateSelections(IEnumerable<Guid> interests, IEnumerable<Guid> teachingSkills,
        IEnumerable<Guid> learningSkills, LearningSkillCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var nextInterests = Snapshot(interests, nameof(interests));
        var nextTeaching = Snapshot(teachingSkills, nameof(teachingSkills));
        var nextLearning = Snapshot(learningSkills, nameof(learningSkills));
        catalogue.ValidateNewSelections(nextTeaching, nextLearning);
        UpdateSelections(nextInterests, nextTeaching, nextLearning);
    }

    public PersonalGoal CreateGoal(string text)
    {
        RequireGoalText(text);
        var goal = new PersonalGoal(Guid.NewGuid(), text);
        Goals = Array.AsReadOnly(Goals.Append(goal).ToArray());
        return goal;
    }

    public PersonalGoal EditGoal(Guid goalId, string text)
    {
        RequireGoalText(text);
        var index = FindGoal(goalId);
        var next = Goals.ToArray();
        var goal = new PersonalGoal(goalId, text);
        next[index] = goal;
        Goals = Array.AsReadOnly(next);
        return goal;
    }

    public void RemoveGoal(Guid goalId)
    {
        var index = FindGoal(goalId);
        Goals = Array.AsReadOnly(Goals.Where((_, i) => i != index).ToArray());
    }

    private int FindGoal(Guid goalId)
    {
        RequireId(goalId, nameof(goalId));
        for (var i = 0; i < Goals.Count; i++)
            if (Goals[i].Id == goalId) return i;
        // This is a local domain lookup, never a response revealing persisted resource existence.
        throw new KeyNotFoundException("Learning goal is not present in this domain instance.");
    }

    private static IReadOnlyList<Guid> Snapshot(IEnumerable<Guid> selections, string parameter)
    {
        ArgumentNullException.ThrowIfNull(selections, parameter);
        var values = selections.ToArray();
        var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            RequireId(value, parameter);
            if (!seen.Add(value)) throw new ArgumentException("Duplicate Learning selection.", parameter);
        }
        return Array.AsReadOnly(values);
    }

    private static void RequireId(Guid id, string parameter)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty identity is required.", parameter);
    }

    private static void RequireGoalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Learning goal text is required.", nameof(text));
        // No trimming, normalization, numerical length limit, deadline or progress rule is chosen.
    }

    public override string ToString() => "Learning preferences [private state omitted; not authorization evidence]";

    /// <summary>Immutable private goal snapshot. No sharing or completion API is supplied.</summary>
    public sealed class PersonalGoal
    {
        public Guid Id { get; }
        public string Text { get; }
        public bool IsPrivate => true;

        internal PersonalGoal(Guid id, string text) { Id = id; Text = text; }

        public override string ToString() => "Private Learning goal [text and identity omitted]";
    }
}
