namespace Andril.Learning.Growth;

/// <summary>Provenance categories, not a caller-controlled verification claim.
/// Verified has no construction or promotion path in this independent foundation.</summary>
public enum LearningEvidenceKind { SelfReported = 1, Verified = 2 }

/// <summary>
/// DEC-20261120-LRN04-01: immutable private self-report provenance and skill-reference foundation.
/// Exact evidence content fields/categories are pending; this is not a complete submission schema.
/// Account/profile/skill IDs are unverified references, not ownership, eligibility or catalogue proof.
/// M2 must authorize each future submission/load/view and enforce private storage and visibility.
/// No verification authority, XP conversion, skill-gap/proficiency analysis or sharing API exists.
/// Missing evidence is not proof of a missing skill. Do not expose this domain object through an API.
/// </summary>
public sealed class LearningGrowthEvidence
{
    public Guid Id { get; }
    public Guid AccountId { get; }
    public Guid LearningProfileId { get; }
    public string Module => "learning";
    public LearningEvidenceKind Kind => LearningEvidenceKind.SelfReported;
    public bool IsPrivate => true;
    public IReadOnlyList<Guid> SkillIds { get; }

    private LearningGrowthEvidence(Guid id, Guid accountId, Guid learningProfileId, IReadOnlyList<Guid> skillIds)
    { Id = id; AccountId = accountId; LearningProfileId = learningProfileId; SkillIds = skillIds; }

    /// <summary>Record self-declared provenance after external permission checks. Skill references
    /// use the existing opaque Guid convention, without managing preferences or inventing a catalogue.
    /// No minimum/maximum association count or text/normalization/evidence-body policy is selected.</summary>
    public static LearningGrowthEvidence RecordSelfReport(Guid id, Guid accountId, Guid learningProfileId,
        IEnumerable<Guid> skillIds)
    {
        RequireId(id, nameof(id)); RequireId(accountId, nameof(accountId)); RequireId(learningProfileId, nameof(learningProfileId));
        ArgumentNullException.ThrowIfNull(skillIds);
        var skills = skillIds.ToArray();
        var distinct = new HashSet<Guid>();
        foreach (var skill in skills)
        {
            RequireId(skill, nameof(skillIds));
            if (!distinct.Add(skill)) throw new ArgumentException("A skill identity may only be associated once.", nameof(skillIds));
        }
        return new(id, accountId, learningProfileId, Array.AsReadOnly(skills));
    }

    private static void RequireId(Guid id, string parameter)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty identity is required.", parameter);
    }

    public override string ToString() => "Private self-reported Learning evidence [identities and associations omitted]";
}
