using System.Text.Json.Serialization;

namespace Andril.Learning.Entities;

/// <summary>Selection purposes already represented by LearningPreferences; not exchange terms.</summary>
public enum LearningSkillSelectionKind { Teaching = 1, Learning = 2 }

/// <summary>
/// Learning-owned private persistence entity for one selected skill. No table, mapping,
/// migration, repository or API is registered here. A supplied preferences instance and its
/// profile reference are not authentication, ownership or catalogue-validity evidence.
/// Future protected persistence must freshly authorize the Learning profile through M2,
/// enforce the Learning-only profile relationship and atomically reconcile selections,
/// uniqueness and the existing unsupported teaching/learning overlap policy.
/// Never expose this entity as an API DTO or use its existence as onboarding evidence.
/// </summary>
public sealed class LearningSkillSelection
{
    [JsonIgnore]
    public Guid Id { get; private set; }
    [JsonIgnore]
    public Guid LearningProfileId { get; private set; }
    [JsonIgnore]
    public string Module { get; private set; } = "learning";
    [JsonIgnore]
    public Guid SkillId { get; private set; }
    [JsonIgnore]
    public LearningSkillSelectionKind Kind { get; private set; }

    // Constructor binding is available to a future ORM; it establishes structural validity only.
    private LearningSkillSelection(Guid id, Guid learningProfileId, Guid skillId,
        LearningSkillSelectionKind kind)
    {
        if (id == Guid.Empty) throw new ArgumentException("A selection ID is required.", nameof(id));
        if (learningProfileId == Guid.Empty)
            throw new ArgumentException("A Learning profile reference is required.", nameof(learningProfileId));
        if (skillId == Guid.Empty) throw new ArgumentException("A skill reference is required.", nameof(skillId));
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unsupported skill selection kind.", nameof(kind));
        Id = id;
        LearningProfileId = learningProfileId;
        SkillId = skillId;
        Kind = kind;
    }

    /// <summary>
    /// Copy a currently selected opaque skill reference without modifying the preferences.
    /// The caller supplies a stable server-owned persistence ID after external authorization;
    /// matching local selections neither verifies the catalogue nor grants persistent access.
    /// </summary>
    public static LearningSkillSelection CreateFromPreferences(Guid id, LearningPreferences preferences,
        Guid skillId, LearningSkillSelectionKind kind)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var selections = kind switch
        {
            LearningSkillSelectionKind.Teaching => preferences.TeachingSkills,
            LearningSkillSelectionKind.Learning => preferences.LearningSkills,
            _ => throw new ArgumentException("Unsupported skill selection kind.", nameof(kind))
        };
        if (skillId == Guid.Empty) throw new ArgumentException("A skill reference is required.", nameof(skillId));
        if (!selections.Contains(skillId))
            throw new ArgumentException("The skill is not selected for this purpose.", nameof(skillId));
        return new(id, preferences.LearningProfileId, skillId, kind);
    }

    public override string ToString() => "Private Learning skill selection [identity, profile and skill omitted]";
}
