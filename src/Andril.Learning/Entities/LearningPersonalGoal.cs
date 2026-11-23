using System.Text.Json.Serialization;

namespace Andril.Learning.Entities;

/// <summary>
/// Private Learning-owned persistence snapshot of an existing personal goal. No table,
/// mapping, migration, repository or API is registered. Supplied preferences and profile
/// references are not authentication, ownership or onboarding-completion evidence.
/// Future persistence must freshly authorize the Learning profile through M2 and enforce
/// its Learning-only relationship. No lifecycle, concurrency or deletion policy is supplied.
/// Never expose this entity as an API DTO.
/// </summary>
public sealed class LearningPersonalGoal
{
    [JsonIgnore]
    public Guid Id { get; private set; }
    [JsonIgnore]
    public Guid LearningProfileId { get; private set; }
    [JsonIgnore]
    public string Module { get; private set; } = "learning";
    [JsonIgnore]
    public string Text { get; private set; }

    // Constructor binding is available to a future ORM; structural validity is not authorization.
    private LearningPersonalGoal(Guid id, Guid learningProfileId, string text)
    {
        if (id == Guid.Empty) throw new ArgumentException("A personal goal ID is required.", nameof(id));
        if (learningProfileId == Guid.Empty)
            throw new ArgumentException("A Learning profile reference is required.", nameof(learningProfileId));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Learning goal text is required.", nameof(text));
        Id = id;
        LearningProfileId = learningProfileId;
        Text = text;
    }

    /// <summary>
    /// Copy the current local goal without modifying preferences or allocating a different goal ID.
    /// Existing domain edits can produce a replacement snapshot with the same identity; earlier
    /// snapshots remain unchanged. A removed goal cannot be copied from these preferences.
    /// Text is not trimmed, normalized or length-limited. Removal here implies no database purge.
    /// </summary>
    public static LearningPersonalGoal CreateFromPreferences(LearningPreferences preferences, Guid goalId)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (goalId == Guid.Empty) throw new ArgumentException("A personal goal ID is required.", nameof(goalId));
        var goal = preferences.Goals.SingleOrDefault(candidate => candidate.Id == goalId)
            ?? throw new KeyNotFoundException("Learning goal is not present in this domain instance.");
        return new(goal.Id, preferences.LearningProfileId, goal.Text);
    }

    public override string ToString() => "Private Learning personal goal [identity, profile and text omitted]";
}
