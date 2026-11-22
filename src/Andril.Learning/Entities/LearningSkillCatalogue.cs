using System.Text.Json.Serialization;

namespace Andril.Learning.Entities;

/// <summary>Catalogue lifecycle facts; these values do not grant catalogue-management permissions.</summary>
public enum LearningSkillCatalogueStatus { Active = 1, Retired = 2, Superseded = 3, Merged = 4 }

/// <summary>
/// Immutable offline Learning catalogue snapshot for validating NEW selections under
/// ANDRIL-M5-LRN-PRODUCT-20261122-01 (LRN-PD-01/02/03).
/// A future trusted boundary must supply a complete, current authoritative catalogue after
/// successful reads; client-supplied entries are not authority. Missing/failed reads must not
/// become an empty catalogue or successful onboarding evidence. No reader is implemented here.
/// Validation is local to this snapshot, not authentication, persistence or concurrency control.
/// Existing preferences and historical selections are never changed, removed or remapped.
/// Display names are deliberately outside eligibility: renaming does not replace an identity.
/// </summary>
public sealed class LearningSkillCatalogue
{
    private readonly Dictionary<Guid, Skill> skills;

    public LearningSkillCatalogue(IEnumerable<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var snapshot = new Dictionary<Guid, Skill>();
        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill);
            // One identity must have one unambiguous set of facts in a supplied snapshot.
            if (!snapshot.TryAdd(skill.Id, skill))
                throw new ArgumentException("Ambiguous Learning catalogue identity.", nameof(skills));
        }
        this.skills = snapshot;
    }

    /// <summary>
    /// Require approved active catalogue identities for both proposed NEW selection lists.
    /// Duplicates within either list are rejected; the same identity across lists is allowed.
    /// Does not update preferences or revalidate/remove historical selections. Future writes
    /// must coordinate fresh catalogue validation, M2 authorization and atomic persistence.
    /// This method must not be used to infer that an entire stored profile is eligible.
    /// </summary>
    public void ValidateNewSelections(IEnumerable<Guid> teachingSkills, IEnumerable<Guid> learningSkills)
    {
        ArgumentNullException.ThrowIfNull(teachingSkills);
        ArgumentNullException.ThrowIfNull(learningSkills);
        ValidateList(teachingSkills, nameof(teachingSkills));
        ValidateList(learningSkills, nameof(learningSkills));
    }

    private void ValidateList(IEnumerable<Guid> selections, string parameter)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in selections)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("A non-empty skill identity is required.", parameter);
            if (!seen.Add(id))
                throw new ArgumentException("Duplicate Learning selection.", parameter);
            if (!skills.TryGetValue(id, out var skill) || !skill.IsApproved ||
                skill.Status != LearningSkillCatalogueStatus.Active)
                // Do not reveal catalogue existence, lifecycle details or supplied identifiers.
                throw new ArgumentException("Skill is not eligible for a new Learning selection.", parameter);
        }
    }

    public override string ToString() => "Learning catalogue snapshot [not authorization evidence]";

    /// <summary>
    /// Immutable eligibility facts for one stable skill identity, supplied by a future trusted
    /// catalogue reader. Construction does not approve a skill or perform a lifecycle change.
    /// No display-name policy, replacement identity, profile data or other-module data is held.
    /// </summary>
    public sealed class Skill
    {
        [JsonIgnore]
        public Guid Id { get; }
        [JsonIgnore]
        public bool IsApproved { get; }
        [JsonIgnore]
        public LearningSkillCatalogueStatus Status { get; }

        public Skill(Guid id, bool isApproved, LearningSkillCatalogueStatus status)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("A non-empty skill identity is required.", nameof(id));
            if (!Enum.IsDefined(status))
                throw new ArgumentException("Unsupported Learning catalogue status.", nameof(status));
            Id = id;
            IsApproved = isApproved;
            Status = status;
        }

        public override string ToString() => "Learning catalogue skill [identity and eligibility facts omitted]";
    }
}
