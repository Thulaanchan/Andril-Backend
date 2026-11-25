using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Learning;

internal static class LearningRevisionConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var profile = model.Entity<LearningProfileKeyRow>();
        profile.ToTable("private_module_profiles", "andril_identity", table => table.ExcludeFromMigrations());
        profile.HasKey(x => x.Id).HasName("pk_private_module_profiles");
        profile.HasAlternateKey(x => new { x.Id, x.Module }).HasName("AK_private_module_profiles_id_module");
        profile.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        profile.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        var revision = model.Entity<LearningProfileRevisionRow>();
        revision.ToTable("learning_profile_revisions", "andril_learning", table =>
        {
            table.HasCheckConstraint("ck_learning_revision_module", "module = 'learning'");
            table.HasCheckConstraint("ck_learning_revision_version", "version >= 0");
            table.HasCheckConstraint("ck_learning_revision_profile", "learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_learning_revision_account", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_learning_revision_generation", "generation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        revision.HasKey(x => new { x.LearningProfileId, x.Module }).HasName("pk_learning_profile_revisions");
        revision.Property(x => x.LearningProfileId).HasColumnName("learning_profile_id").ValueGeneratedNever();
        revision.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        revision.Property(x => x.AccountId).HasColumnName("account_id");
        revision.Property(x => x.GenerationId).HasColumnName("generation_id");
        revision.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        revision.Property(x => x.LastMutationTransaction).HasColumnName("last_mutation_transaction").HasColumnType("text");
        revision.Property(x => x.SelectionIds).HasColumnName("selection_ids").HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]");
        revision.Property(x => x.GoalIds).HasColumnName("goal_ids").HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]");
        revision.HasIndex(x => x.GenerationId).IsUnique().HasDatabaseName("uq_learning_revision_generation");
        revision.HasOne<LearningProfileKeyRow>().WithMany().HasForeignKey(x => new { x.LearningProfileId, x.Module })
            .HasPrincipalKey(x => new { x.Id, x.Module }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_learning_revision_profile");

        model.Entity<Andril.Learning.Entities.LearningSkillSelection>().HasOne<LearningProfileRevisionRow>().WithMany()
            .HasForeignKey(x => new { x.LearningProfileId, x.Module }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_learning_selection_revision");
        model.Entity<Andril.Learning.Entities.LearningPersonalGoal>().HasOne<LearningProfileRevisionRow>().WithMany()
            .HasForeignKey(x => new { x.LearningProfileId, x.Module }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_learning_goal_revision");

        var generation = model.Entity<LearningGenerationRow>();
        generation.ToTable("learning_revision_generations", "andril_learning", table =>
            table.HasCheckConstraint("ck_learning_generation_nonempty", "generation_id <> '00000000-0000-0000-0000-000000000000'::uuid"));
        generation.HasKey(x => x.GenerationId).HasName("pk_learning_revision_generations");
        generation.Property(x => x.GenerationId).HasColumnName("generation_id").ValueGeneratedNever();
        revision.HasOne<LearningGenerationRow>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_learning_revision_generation");

        var receipt = model.Entity<LearningMutationReceiptRow>();
        receipt.ToTable("learning_mutation_receipts", "andril_learning");
        receipt.HasKey(x => x.OperationId).HasName("pk_learning_mutation_receipts");
        receipt.Property(x => x.OperationId).HasColumnName("operation_id").ValueGeneratedNever();
        receipt.Property(x => x.LearningProfileId).HasColumnName("learning_profile_id");
        receipt.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        receipt.Property(x => x.GenerationId).HasColumnName("generation_id");
        receipt.Property(x => x.ExpectedVersion).HasColumnName("expected_version");
        receipt.Property(x => x.ResultVersion).HasColumnName("result_version");
        receipt.Property(x => x.IntentHash).HasColumnName("intent_hash").HasColumnType("text");
        receipt.HasIndex(x => new { x.LearningProfileId, x.Module }).HasDatabaseName("ix_learning_mutation_receipts_profile");
        receipt.HasOne<LearningProfileRevisionRow>().WithMany().HasForeignKey(x => new { x.LearningProfileId, x.Module })
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_learning_receipt_revision");
    }
}
