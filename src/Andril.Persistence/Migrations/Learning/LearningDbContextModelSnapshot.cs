using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Andril.Persistence.Migrations.Learning;

[DbContext(typeof(LearningDbContext))]
internal sealed class LearningDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
        => LearningSnapshotV1.Configure(modelBuilder);
}

// Frozen shadow-property snapshot. Never delegate to live runtime mappings: doing so would
// conceal later model changes from EF's pending-migration verification.
internal static class LearningSnapshotV1
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.13").HasAnnotation("Relational:MaxIdentifierLength", 63);
        modelBuilder.UseIdentityByDefaultColumns();
        modelBuilder.HasDefaultSchema("andril_learning");
        const string prefix = "Andril.Persistence.Repositories.Learning.";
        const string selection = "Andril.Learning.Entities.LearningSkillSelection";
        const string goal = "Andril.Learning.Entities.LearningPersonalGoal";
        foreach (var name in new[] { selection, goal })
        {
            modelBuilder.Entity(name, b =>
            {
                var s = name == selection;
                b.UsePropertyAccessMode(PropertyAccessMode.Property);
                b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
                b.Property<Guid>("LearningProfileId").HasColumnType("uuid").HasColumnName("learning_profile_id");
                b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
                b.HasKey("Id").HasName(s ? "pk_learning_skill_selections" : "pk_learning_personal_goals");
                if (s)
                {
                    b.Property<Guid>("SkillId").HasColumnType("uuid").HasColumnName("skill_id");
                    b.Property<int>("Kind").HasColumnType("integer").HasColumnName("kind");
                    b.HasIndex("LearningProfileId", "Module", "Kind", "SkillId").IsUnique().HasDatabaseName("uq_learning_skill_selections_profile_purpose_skill");
                }
                else
                {
                    b.Property<string>("Text").IsRequired().HasColumnType("text").HasColumnName("text");
                    b.HasIndex("LearningProfileId", "Module").HasDatabaseName("ix_learning_personal_goals_profile_module");
                }
                b.ToTable(s ? "learning_skill_selections" : "learning_personal_goals", "andril_learning", table =>
                {
                    var c = s ? "ck_learning_skill_selection_" : "ck_learning_personal_goal_";
                    table.HasCheckConstraint(c + "module", "module = 'learning'");
                    table.HasCheckConstraint(c + "id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.HasCheckConstraint(c + "profile", "learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    if (s)
                    {
                        table.HasCheckConstraint(c + "kind", "kind IN (1, 2)");
                        table.HasCheckConstraint(c + "skill", "skill_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    }
                });
                b.HasOne(prefix + "LearningProfileRevisionRow", null).WithMany().HasForeignKey("LearningProfileId", "Module")
                    .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName(s ? "fk_learning_selection_revision" : "fk_learning_goal_revision");
            });
        }
        modelBuilder.Entity(prefix + "LearningProfileKeyRow", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.HasKey("Id").HasName("pk_private_module_profiles");
            b.HasAlternateKey("Id", "Module").HasName("AK_private_module_profiles_id_module");
            b.ToTable("private_module_profiles", "andril_identity", t => t.ExcludeFromMigrations());
        });
        modelBuilder.Entity(prefix + "LearningGenerationRow", b =>
        {
            b.Property<Guid>("GenerationId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("generation_id");
            b.HasKey("GenerationId").HasName("pk_learning_revision_generations");
            b.ToTable("learning_revision_generations", "andril_learning", t => t.HasCheckConstraint("ck_learning_generation_nonempty", "generation_id <> '00000000-0000-0000-0000-000000000000'::uuid"));
        });
        modelBuilder.Entity(prefix + "LearningProfileRevisionRow", b =>
        {
            b.Property<Guid>("LearningProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("AccountId").HasColumnType("uuid").HasColumnName("account_id");
            b.Property<Guid>("GenerationId").HasColumnType("uuid").HasColumnName("generation_id");
            b.Property<long>("Version").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("version");
            b.Property<string>("LastMutationTransaction").HasColumnType("text").HasColumnName("last_mutation_transaction");
            b.Property<Guid[]>("SelectionIds").IsRequired().ValueGeneratedOnAdd().HasColumnType("uuid[]").HasColumnName("selection_ids").HasDefaultValueSql("'{}'::uuid[]");
            b.Property<Guid[]>("GoalIds").IsRequired().ValueGeneratedOnAdd().HasColumnType("uuid[]").HasColumnName("goal_ids").HasDefaultValueSql("'{}'::uuid[]");
            b.HasKey("LearningProfileId", "Module").HasName("pk_learning_profile_revisions");
            b.HasIndex("GenerationId").IsUnique().HasDatabaseName("uq_learning_revision_generation");
            b.ToTable("learning_profile_revisions", "andril_learning", t =>
            {
                t.HasCheckConstraint("ck_learning_revision_module", "module = 'learning'");
                t.HasCheckConstraint("ck_learning_revision_version", "version >= 0");
                t.HasCheckConstraint("ck_learning_revision_profile", "learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_revision_account", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_learning_revision_generation", "generation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            });
            b.HasOne(prefix + "LearningGenerationRow", null).WithMany().HasForeignKey("GenerationId").OnDelete(DeleteBehavior.Restrict).IsRequired().HasConstraintName("fk_learning_revision_generation");
            b.HasOne(prefix + "LearningProfileKeyRow", null).WithMany().HasForeignKey("LearningProfileId", "Module").HasPrincipalKey("Id", "Module")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_learning_revision_profile");
        });
        modelBuilder.Entity(prefix + "LearningMutationReceiptRow", b =>
        {
            b.Property<Guid>("OperationId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("operation_id");
            b.Property<Guid>("LearningProfileId").HasColumnType("uuid").HasColumnName("learning_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<Guid>("GenerationId").HasColumnType("uuid").HasColumnName("generation_id");
            b.Property<long>("ExpectedVersion").HasColumnType("bigint").HasColumnName("expected_version");
            b.Property<long>("ResultVersion").HasColumnType("bigint").HasColumnName("result_version");
            b.Property<string>("IntentHash").IsRequired().HasColumnType("text").HasColumnName("intent_hash");
            b.HasKey("OperationId").HasName("pk_learning_mutation_receipts");
            b.HasIndex("LearningProfileId", "Module").HasDatabaseName("ix_learning_mutation_receipts_profile");
            b.ToTable("learning_mutation_receipts", "andril_learning");
            b.HasOne(prefix + "LearningProfileRevisionRow", null).WithMany().HasForeignKey("LearningProfileId", "Module")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_learning_receipt_revision");
        });
    }
}
