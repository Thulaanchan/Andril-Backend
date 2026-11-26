using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Migrations.Learning;

// Frozen Portfolio migration target, preserving the earlier V1 and V2 targets.
internal static class LearningSnapshotV3
{
    internal static void Configure(ModelBuilder model)
    {
        LearningSnapshotV2.Configure(model);
        model.Entity("Andril.Persistence.Repositories.Learning.LearningPortfolioRow", b =>
        {
            b.Property<Guid>("OwnerLearningProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("owner_learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("PortfolioId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("portfolio_id");
            b.Property<int>("Template").HasColumnType("integer").HasColumnName("template");
            b.Property<string>("Projects").IsRequired().HasColumnType("jsonb").HasColumnName("projects");
            b.HasKey("OwnerLearningProfileId", "Module", "PortfolioId").HasName("pk_learning_portfolios");
            b.ToTable("learning_portfolios", "andril_learning", t =>
            {
                t.HasCheckConstraint("ck_learning_portfolio_owner", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_portfolio_id", "portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_portfolio_module", "module = 'learning'");
                t.HasCheckConstraint("ck_learning_portfolio_template", "template IN (1,2,3)");
                t.HasCheckConstraint("ck_learning_portfolio_projects", "jsonb_typeof(projects) = 'array'");
            });
        });
    }
}
