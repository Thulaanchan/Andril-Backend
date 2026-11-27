using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Migrations.Learning;

// Frozen import migration target; earlier migration snapshots stay unchanged.
internal static class LearningSnapshotV4
{
    internal static void Configure(ModelBuilder m)
    {
        LearningSnapshotV3.Configure(m);
        m.Entity("Andril.Persistence.Repositories.Learning.LearningPortfolioRow", b =>
        {
            b.Property<Guid>("ContentVersion").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("content_version");
            b.ToTable("learning_portfolios", "andril_learning", t => t.HasCheckConstraint("ck_learning_portfolio_version", "content_version <> '00000000-0000-0000-0000-000000000000'::uuid"));
        });
        m.Entity("Andril.Persistence.Repositories.Learning.LearningProjectImportReviewRow", b =>
        {
            b.Property<Guid>("OwnerLearningProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("owner_learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("ReviewId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("review_id");
            b.Property<Guid>("PortfolioId").HasColumnType("uuid").HasColumnName("portfolio_id");
            b.Property<Guid>("CvId").HasColumnType("uuid").HasColumnName("cv_id");
            b.Property<Guid>("SourceVersion").HasColumnType("uuid").HasColumnName("source_version");
            b.Property<string>("SourceFingerprint").IsRequired().HasColumnType("text").HasColumnName("source_fingerprint");
            b.Property<string>("Projects").IsRequired().HasColumnType("jsonb").HasColumnName("projects");
            b.Property<bool>("Confirmed").HasColumnType("boolean").HasColumnName("confirmed");
            b.HasKey("OwnerLearningProfileId", "Module", "ReviewId").HasName("pk_learning_project_import_reviews");
            b.ToTable("learning_project_import_reviews", "andril_learning", t =>
            {
                t.HasCheckConstraint("ck_learning_import_review_ids", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND source_version <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_import_review_module", "module = 'learning'");
                t.HasCheckConstraint("ck_learning_import_review_projects", "jsonb_typeof(projects) = 'array' AND jsonb_array_length(projects) > 0");
            });
        });
        m.Entity("Andril.Persistence.Repositories.Learning.LearningProjectImportOriginRow", b =>
        {
            b.Property<Guid>("OwnerLearningProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("owner_learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("PortfolioId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("portfolio_id");
            b.Property<Guid>("ProjectId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("project_id");
            b.Property<Guid>("CvId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("cv_id");
            b.Property<Guid>("EntryId").HasColumnType("uuid").HasColumnName("entry_id");
            b.Property<Guid>("ReviewId").HasColumnType("uuid").HasColumnName("review_id");
            b.HasKey("OwnerLearningProfileId", "Module", "PortfolioId", "ProjectId", "CvId").HasName("pk_learning_project_import_origins");
            b.ToTable("learning_project_import_origins", "andril_learning", t =>
            {
                t.HasCheckConstraint("ck_learning_import_origin_ids", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid AND project_id <> '00000000-0000-0000-0000-000000000000'::uuid AND cv_id <> '00000000-0000-0000-0000-000000000000'::uuid AND entry_id <> '00000000-0000-0000-0000-000000000000'::uuid AND review_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_import_origin_module", "module = 'learning'");
            });
        });
    }
}
