using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

public enum CvSectionCategory
{
    PersonalInformation = 1,
    ProfessionalSummary = 2,
    Education = 3,
    WorkExperience = 4,
    Skills = 5,
    Projects = 6,
    CertificationsAndAchievements = 7
}

/// <summary>Immutable PRIVATE EDITABLE section foundation, not a DTO or renderable payload.
/// Categories identify seven logical sections; entries are distinct repeatable items within them.
/// Detailed name/contact/title, summary, education, employment, skill, project and achievement
/// field schemas remain pending. Entry text may contain contact information in ANY category.
/// Default JSON deliberately omits entries/text regardless of inclusion choice. A future authorized
/// renderer needs approved field classification and a fresh CV-specific consent check, never a
/// generic section-to-output conversion. No rendering, section-ordering or import policy is supplied.</summary>
public sealed class CvSection
{
    public Guid Id { get; }
    public CvSectionCategory Category { get; }
    [JsonIgnore]
    public IReadOnlyList<Entry> Entries { get; }

    public CvSection(Guid id, CvSectionCategory category, IEnumerable<Entry> entries)
    {
        if (id == Guid.Empty) throw new ArgumentException("A CV section ID is required.", nameof(id));
        if (!Enum.IsDefined(category)) throw new ArgumentException("Unsupported CV section category.", nameof(category));
        ArgumentNullException.ThrowIfNull(entries);
        var copy = entries.ToArray(); var ids = new HashSet<Guid>();
        foreach (var entry in copy)
            if (entry is null || entry.SectionId != id || !ids.Add(entry.Id))
                throw new ArgumentException("Entries must have distinct identities bound to this section.", nameof(entries));
        foreach (var entry in copy) _ = CvStructuredFields.Validate(category, entry.Fields);
        Id = id; Category = category; Entries = Array.AsReadOnly(copy);
    }

    /// <summary>Replace private editable entries explicitly, preserving logical section identity.
    /// No field requirements, text limits or date/normalization policy is invented.</summary>
    public CvSection WithEntries(IEnumerable<Entry> entries) => new(Id, Category, entries);
    public override string ToString() => $"Private CV section ({Category}) [identity and content omitted]";

    public sealed class Entry
    {
        public Guid Id { get; }
        public Guid SectionId { get; }
        [JsonIgnore]
        public string? Text { get; }
        [JsonIgnore] public IReadOnlyDictionary<string,string> Fields { get; }

        public Entry(Guid id, Guid sectionId, string? text) : this(id,sectionId,text,null) { }
        public Entry(Guid id, Guid sectionId, string? text, IReadOnlyDictionary<string,string>? fields)
        {
            if (id == Guid.Empty) throw new ArgumentException("A CV entry ID is required.", nameof(id));
            if (sectionId == Guid.Empty) throw new ArgumentException("A CV section ID is required.", nameof(sectionId));
            Id = id; SectionId = sectionId; Text = text;
            Fields = new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(new Dictionary<string,string>(fields ?? new Dictionary<string,string>(),StringComparer.Ordinal));
        }
        public override string ToString() => "Private editable CV entry [identity and text omitted]";
    }
}
