using System.Text.Json;
using Andril.Learning.CV;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Learning;

internal sealed class LearningCvRow
{
    public Guid OwnerLearningProfileId { get; private set; }
    public string Module { get; private set; } = "learning";
    public Guid CvId { get; private set; }
    public int Template { get; private set; }
    public bool IncludeContactInformation { get; private set; }
    public string Sections { get; private set; } = null!;
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredEntry(Guid Id, Guid SectionId, string? Text, Dictionary<string,string>? Fields = null);
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredSection(Guid Id, CvSectionCategory Category, StoredEntry[] Entries);

    internal static LearningCvRow From(LearningCv cv) => new()
    {
        OwnerLearningProfileId = cv.OwnerLearningProfileId, CvId = cv.Id, Template = (int)cv.Template,
        IncludeContactInformation = cv.IncludeContactInformation,
        Sections = JsonSerializer.Serialize(cv.Sections.Select(s => new StoredSection(s.Id, s.Category,
            s.Entries.Select(e => new StoredEntry(e.Id, e.SectionId, e.Text,e.Fields.Count == 0 ? null : new(e.Fields))).ToArray())))
    };
    internal LearningCv ToDomain()
    {
        var sections = JsonSerializer.Deserialize<StoredSection[]>(Sections) ?? throw new JsonException("Invalid stored CV.");
        if (sections.Any(s => s is null || s.Entries is null || s.Entries.Any(e => e is null))) throw new JsonException("Invalid stored CV.");
        return LearningCv.FromStoredSnapshot(CvId, OwnerLearningProfileId, (CvTemplate)Template,
            sections.Select(s => new CvSection(s.Id, s.Category, s.Entries.Select(e => new CvSection.Entry(e.Id, e.SectionId, e.Text,e.Fields)))), IncludeContactInformation);
    }
    internal void ReplaceContent(LearningCvRow row)
    {
        if (CvId != row.CvId || OwnerLearningProfileId != row.OwnerLearningProfileId || Module != row.Module)
            throw new InvalidOperationException("CV identity is immutable.");
        Template = row.Template; IncludeContactInformation = row.IncludeContactInformation; Sections = row.Sections;
    }
    public override string ToString() => "Private persisted CV [scope, consent and content omitted]";
}

internal static class LearningCvConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var cv = model.Entity<LearningCvRow>();
        cv.ToTable("learning_cvs", "andril_learning", t =>
        {
            t.HasCheckConstraint("ck_learning_cv_owner", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_cv_id", "cv_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_cv_module", "module = 'learning'");
            t.HasCheckConstraint("ck_learning_cv_template", "template IN (1,2,3)");
            t.HasCheckConstraint("ck_learning_cv_sections", "jsonb_typeof(sections) = 'array' AND jsonb_array_length(sections) = 7");
        });
        cv.HasKey(x => new { x.OwnerLearningProfileId, x.Module, x.CvId }).HasName("pk_learning_cvs");
        cv.Property(x => x.OwnerLearningProfileId).HasColumnName("owner_learning_profile_id").ValueGeneratedNever();
        cv.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        cv.Property(x => x.CvId).HasColumnName("cv_id").ValueGeneratedNever();
        cv.Property(x => x.Template).HasColumnName("template");
        cv.Property(x => x.IncludeContactInformation).HasColumnName("include_contact_information").HasDefaultValue(false);
        cv.Property(x => x.Sections).HasColumnName("sections").HasColumnType("jsonb").IsRequired();
    }
}
