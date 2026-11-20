using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>
/// DEC-20261120-LRN05-01: private owner-scoped in-memory CV and editing invariants only.
/// A server-resolved Learning profile reference is required; an ID is NOT authentication or
/// proof of ownership. M2 must freshly authorize creation/load/edit/preview/export. Storage must
/// enforce unique document identity, persistent ownership, current consent and concurrency.
/// No store, autosave, public retrieval, rendering, PDF, Portfolio import or XP effect exists.
/// Private editable content is never a renderable snapshot; don't serialize this as an API DTO.
/// </summary>
public sealed class LearningCv
{
    public Guid Id { get; }
    public Guid OwnerLearningProfileId { get; }
    public string Module => "learning";
    public bool IsPrivate => true;
    public CvTemplate Template { get; private set; }
    public bool IncludeContactInformation { get; private set; }
    [JsonIgnore]
    public IReadOnlyList<CvSection> Sections { get; private set; }

    public LearningCv(Guid id, Guid ownerLearningProfileId, CvTemplate template)
    {
        if (id == Guid.Empty) throw new ArgumentException("A CV ID is required.", nameof(id));
        if (ownerLearningProfileId == Guid.Empty) throw new ArgumentException("A Learning-profile owner ID is required.", nameof(ownerLearningProfileId));
        RequireTemplate(template);
        var sections = Enum.GetValues<CvSectionCategory>()
            .Select(category => new CvSection(Guid.NewGuid(), category, [])).ToArray();
        Id = id; OwnerLearningProfileId = ownerLearningProfileId; Template = template;
        Sections = Array.AsReadOnly(sections);
    }

    public CvSection GetSection(CvSectionCategory category)
    {
        RequireCategory(category);
        return Sections.Single(section => section.Category == category);
    }

    /// <summary>Explicit manual content editing only. Both category and section identity must
    /// match this CV. Entries cannot move implicitly between sections or collide across them.</summary>
    public void EditSection(Guid sectionId, CvSectionCategory category, IEnumerable<CvSection.Entry> entries)
    {
        if (sectionId == Guid.Empty) throw new ArgumentException("A CV section ID is required.", nameof(sectionId));
        RequireCategory(category);
        var index = -1;
        for (var i = 0; i < Sections.Count; i++)
            if (Sections[i].Category == category && Sections[i].Id == sectionId) { index = i; break; }
        if (index < 0) throw new ArgumentException("The section reference does not match this CV and category.", nameof(sectionId));
        var replacement = Sections[index].WithEntries(entries);
        var otherIds = Sections.Where((_, i) => i != index).SelectMany(section => section.Entries).Select(entry => entry.Id).ToHashSet();
        if (replacement.Entries.Any(entry => otherIds.Contains(entry.Id)))
            throw new ArgumentException("Entry identities cannot be reused across CV sections.", nameof(entries));
        var next = Sections.ToArray(); next[index] = replacement;
        Sections = Array.AsReadOnly(next);
    }

    public void SelectTemplate(CvTemplate template)
    {
        RequireTemplate(template);
        Template = template;
    }

    /// <summary>Record an explicit document-specific inclusion choice after external intent/access
    /// checks; this is not a legal consent receipt or authentication evidence. Revocation changes
    /// this CV's current choice only, not previously downloaded/shared copies. No renderer exists:
    /// even consent=true must not turn generic private entries into output without approved field
    /// classification. Persistent preview/export must re-read current consent under its authority.</summary>
    public void SetContactInclusionConsent(Guid cvId, bool include)
    {
        if (cvId == Guid.Empty || cvId != Id) throw new ArgumentException("Contact inclusion choice must target this CV.", nameof(cvId));
        IncludeContactInformation = include;
    }

    private static void RequireTemplate(CvTemplate template)
    {
        if (!Enum.IsDefined(template)) throw new ArgumentException("Unsupported CV template.", nameof(template));
    }
    private static void RequireCategory(CvSectionCategory category)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentException("Unsupported CV section category.", nameof(category));
    }
    public override string ToString() => "Private Learning CV [identity, owner and content omitted]";
}
