using System.Text.Json.Serialization;
using Andril.Learning.Entities;

namespace Andril.Learning.Interfaces;

/// <summary>
/// PROVISIONAL backend-only application contract pending M2/M6 integration review.
/// Public CLR visibility supports backend assemblies; this is not an HTTP/client contract.
/// No implementation, storage, publisher, DI registration or authorization is supplied.
/// </summary>
public interface ILearningSkillCatalogueReader
{
    /// <summary>
    /// Read a complete, current catalogue and its opaque revision from trusted backend authority.
    /// Missing, malformed, untrusted, partial or insufficiently fresh authority yields Unavailable,
    /// never a fabricated empty snapshot. A genuinely empty authoritative catalogue is distinct.
    /// Client-submitted approval/status/revision values must never establish authority.
    /// Honor cancellation before and after asynchronous work: propagate cancellation, not success
    /// or an Unavailable fallback. Do not retry or replace merged skills automatically.
    /// The future adapter must establish completeness/freshness; this interface cannot prove either.
    /// A revision is not a credential, cache lifetime or permission to commit a later write.
    /// </summary>
    Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
}

public enum LearningSkillCatalogueReadState { Available = 1, Unavailable = 2 }

/// <summary>
/// Immutable structural result only. Available asserts that the trusted reader established
/// authority; factory construction does not verify provenance, completeness or freshness.
/// No revision algorithm, catalogue storage, client authority or onboarding grant is defined.
/// Never use as a public DTO or deserialize client input into authoritative evidence.
/// </summary>
public sealed class LearningSkillCatalogueReadResult
{
    [JsonIgnore]
    public LearningSkillCatalogueReadState State { get; }
    [JsonIgnore]
    public LearningSkillCatalogue? Catalogue { get; }
    [JsonIgnore]
    public string? Revision { get; }

    private LearningSkillCatalogueReadResult(LearningSkillCatalogueReadState state,
        LearningSkillCatalogue? catalogue, string? revision)
    {
        State = state;
        Catalogue = catalogue;
        Revision = revision;
    }

    /// <summary>
    /// Package a complete snapshot and nonblank opaque revision after trusted verification.
    /// Revision text is preserved exactly; its format, ordering and validity remain with authority.
    /// The existing catalogue already holds immutable, defensively copied skill facts.
    /// </summary>
    public static LearningSkillCatalogueReadResult Available(LearningSkillCatalogue catalogue, string revision)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (string.IsNullOrWhiteSpace(revision))
            throw new ArgumentException("Catalogue revision evidence is required.", nameof(revision));
        return new(LearningSkillCatalogueReadState.Available, catalogue, revision);
    }

    /// <summary>No catalogue, revision, raw exception, private diagnostic or fallback is supplied.</summary>
    public static LearningSkillCatalogueReadResult Unavailable() =>
        new(LearningSkillCatalogueReadState.Unavailable, null, null);

    public override string ToString() => "Learning catalogue read result [authority evidence omitted]";
}
