using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Migrations.Learning;

// Extend the frozen V1 snapshot without changing its original migration target.
internal static class LearningSnapshotV2
{
    internal static void Configure(ModelBuilder model)
    {
        LearningSnapshotV1.Configure(model);
        model.Entity("Andril.Persistence.Repositories.Learning.LearningCvRow", b =>
        {
            b.Property<Guid>("OwnerLearningProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("owner_learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("CvId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("cv_id");
            b.Property<int>("Template").HasColumnType("integer").HasColumnName("template");
            b.Property<bool>("IncludeContactInformation").ValueGeneratedOnAdd().HasColumnType("boolean").HasColumnName("include_contact_information").HasDefaultValue(false);
            b.Property<string>("Sections").IsRequired().HasColumnType("jsonb").HasColumnName("sections");
            b.HasKey("OwnerLearningProfileId", "Module", "CvId").HasName("pk_learning_cvs");
            b.ToTable("learning_cvs", "andril_learning", t =>
            {
                t.HasCheckConstraint("ck_learning_cv_owner", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_cv_id", "cv_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_cv_module", "module = 'learning'");
                t.HasCheckConstraint("ck_learning_cv_template", "template IN (1,2,3)");
                t.HasCheckConstraint("ck_learning_cv_sections", "jsonb_typeof(sections) = 'array' AND jsonb_array_length(sections) = 7");
            });
        });
    }
}
