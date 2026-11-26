using System.Text.Json;
using Andril.Learning.CV;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Learning;

internal sealed class LearningProjectImportReviewRow
{
    public Guid OwnerLearningProfileId { get; set; }
    public string Module { get; set; } = "learning";
    public Guid ReviewId { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid CvId { get; set; }
    public Guid SourceVersion { get; set; }
    public string SourceFingerprint { get; set; } = null!;
    public string Projects { get; set; } = null!;
    public bool Confirmed { get; set; }
    internal sealed record SelectedProject(Guid Id, string? Title, string? Description);
    internal SelectedProject[] Selected()
    {
        var p = JsonSerializer.Deserialize<SelectedProject[]>(Projects) ?? throw new JsonException("Invalid stored review.");
        if (p.Length == 0 || p.Any(x => x is null || x.Id == Guid.Empty) || p.Select(x => x.Id).Distinct().Count() != p.Length)
            throw new JsonException("Invalid stored review.");
        return p;
    }
    internal ProjectImportReview ToDomain() => new(ReviewId, PortfolioId, CvId, Selected().Select(x => new ReviewedPortfolioProject(x.Id, x.Title, x.Description)));
    public override string ToString() => "Private persisted import review [content omitted]";
}

internal sealed class LearningProjectImportOriginRow
{
    public Guid OwnerLearningProfileId { get; set; }
    public string Module { get; set; } = "learning";
    public Guid PortfolioId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CvId { get; set; }
    public Guid EntryId { get; set; }
    public Guid ReviewId { get; set; }
    public override string ToString() => "Private import origin [identities omitted]";
}

internal static class LearningProjectImportConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var r = model.Entity<LearningProjectImportReviewRow>();
        r.ToTable("learning_project_import_reviews", "andril_learning", t =>
        {
            t.HasCheckConstraint("ck_learning_import_review_ids", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND source_version <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_import_review_module", "module = 'learning'");
            t.HasCheckConstraint("ck_learning_import_review_projects", "jsonb_typeof(projects) = 'array' AND jsonb_array_length(projects) > 0");
        });
        r.HasKey(x => new { x.OwnerLearningProfileId, x.Module, x.ReviewId }).HasName("pk_learning_project_import_reviews");
        r.Property(x => x.OwnerLearningProfileId).HasColumnName("owner_learning_profile_id").ValueGeneratedNever();
        r.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        r.Property(x => x.ReviewId).HasColumnName("review_id").ValueGeneratedNever();
        r.Property(x => x.PortfolioId).HasColumnName("portfolio_id"); r.Property(x => x.CvId).HasColumnName("cv_id");
        r.Property(x => x.SourceVersion).HasColumnName("source_version");
        r.Property(x => x.SourceFingerprint).HasColumnName("source_fingerprint").IsRequired();
        r.Property(x => x.Projects).HasColumnName("projects").HasColumnType("jsonb").IsRequired();
        r.Property(x => x.Confirmed).HasColumnName("confirmed");
        var o = model.Entity<LearningProjectImportOriginRow>();
        o.ToTable("learning_project_import_origins", "andril_learning", t =>
        {
            t.HasCheckConstraint("ck_learning_import_origin_ids", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND project_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND entry_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_import_origin_module", "module = 'learning'");
        });
        o.HasKey(x => new { x.OwnerLearningProfileId, x.Module, x.PortfolioId, x.ProjectId, x.CvId }).HasName("pk_learning_project_import_origins");
        o.Property(x => x.OwnerLearningProfileId).HasColumnName("owner_learning_profile_id").ValueGeneratedNever();
        o.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        o.Property(x => x.PortfolioId).HasColumnName("portfolio_id").ValueGeneratedNever();
        o.Property(x => x.ProjectId).HasColumnName("project_id").ValueGeneratedNever();
        o.Property(x => x.CvId).HasColumnName("cv_id").ValueGeneratedNever();
        o.Property(x => x.EntryId).HasColumnName("entry_id"); o.Property(x => x.ReviewId).HasColumnName("review_id");
    }
}
