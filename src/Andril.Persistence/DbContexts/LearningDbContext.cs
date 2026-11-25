using Andril.Learning.Entities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Isolated, undeployed Learning model only. No runtime registration, connection configuration,
/// migration, authorization, RLS or cross-schema relationship is supplied. Profile references
/// are not access grants; future repositories must enforce fresh M2 Learning ownership checks.
/// The future provider-options owner must configure migration history as
/// andril_learning.__EFMigrationsHistory_Learning before any migration operation.
/// </summary>
public sealed class LearningDbContext(DbContextOptions<LearningDbContext> options) : DbContext(options)
{
    public DbSet<LearningSkillSelection> SkillSelections => Set<LearningSkillSelection>();
    public DbSet<LearningPersonalGoal> PersonalGoals => Set<LearningPersonalGoal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("andril_learning");

        var selections = modelBuilder.Entity<LearningSkillSelection>();
        selections.UsePropertyAccessMode(PropertyAccessMode.Property);
        selections.ToTable("learning_skill_selections", "andril_learning", table =>
        {
            table.HasCheckConstraint("ck_learning_skill_selection_module", "module = 'learning'");
            table.HasCheckConstraint("ck_learning_skill_selection_kind", "kind IN (1, 2)");
            table.HasCheckConstraint("ck_learning_skill_selection_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_learning_skill_selection_profile", "learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_learning_skill_selection_skill", "skill_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        selections.HasKey(selection => selection.Id).HasName("pk_learning_skill_selections");
        selections.Property(selection => selection.Id)
            .HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        selections.Property(selection => selection.LearningProfileId)
            .HasColumnName("learning_profile_id").HasColumnType("uuid").IsRequired();
        selections.Property(selection => selection.Module)
            .HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16).IsRequired();
        selections.Property(selection => selection.SkillId)
            .HasColumnName("skill_id").HasColumnType("uuid").IsRequired();
        selections.Property(selection => selection.Kind)
            .HasColumnName("kind").HasConversion<int>().HasColumnType("integer").IsRequired();
        // Existing domain/repository snapshot rules reject duplicates within a purpose, while
        // explicitly allowing the same skill in both Teaching and Learning. No catalogue FK.
        selections.HasIndex(selection => new { selection.LearningProfileId, selection.Module, selection.Kind, selection.SkillId })
            .IsUnique().HasDatabaseName("uq_learning_skill_selections_profile_purpose_skill");

        var goals = modelBuilder.Entity<LearningPersonalGoal>();
        goals.UsePropertyAccessMode(PropertyAccessMode.Property);
        goals.ToTable("learning_personal_goals", "andril_learning", table =>
        {
            table.HasCheckConstraint("ck_learning_personal_goal_module", "module = 'learning'");
            table.HasCheckConstraint("ck_learning_personal_goal_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_learning_personal_goal_profile", "learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        goals.HasKey(goal => goal.Id).HasName("pk_learning_personal_goals");
        goals.Property(goal => goal.Id)
            .HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        goals.Property(goal => goal.LearningProfileId)
            .HasColumnName("learning_profile_id").HasColumnType("uuid").IsRequired();
        goals.Property(goal => goal.Module)
            .HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16).IsRequired();
        goals.Property(goal => goal.Text)
            .HasColumnName("text").HasColumnType("text").IsRequired();
        goals.HasIndex(goal => new { goal.LearningProfileId, goal.Module })
            .HasDatabaseName("ix_learning_personal_goals_profile_module");

        // Provisional Learning-owned constraints only. No ownership FK, cascade or catalogue
        // relationship is inferred. Shared profile keys/delete behavior and runtime grants/RLS
        // require coordinated M2/M6 integration. No goal-text uniqueness or retention policy.
        // Required columns do not replace domain validation, authorization or database role review.
    }
}
