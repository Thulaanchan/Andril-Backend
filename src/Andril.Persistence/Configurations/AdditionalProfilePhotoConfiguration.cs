using Andril.Common.Accounts.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class AdditionalProfilePhotoConfiguration : IEntityTypeConfiguration<AdditionalProfilePhotoRecord>
{
    public void Configure(EntityTypeBuilder<AdditionalProfilePhotoRecord> b)
    {
        b.ToTable("profile_additional_photos", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_additional_photo_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_additional_photo_slot", "slot_index BETWEEN 1 AND 3");
            t.HasCheckConstraint("ck_additional_photo_module", "module IN ('learning', 'hobbies', 'social')");
            t.HasCheckConstraint("ck_additional_photo_size", "file_size_bytes BETWEEN 1 AND 5242880");
            t.HasCheckConstraint("ck_additional_photo_timestamps", "updated_at_utc >= created_at_utc");
        });

        b.HasKey(x => x.Id).HasName("pk_profile_additional_photos");

        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.ProfileId).HasColumnName("profile_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(16).HasColumnType("character varying(16)").IsRequired();
        b.Property(x => x.SlotIndex).HasColumnName("slot_index").HasColumnType("integer").IsRequired();
        b.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(128).HasColumnType("character varying(128)").IsRequired();
        b.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(256).HasColumnType("character varying(256)");
        b.Property(x => x.MediaType).HasColumnName("media_type").HasMaxLength(32).HasColumnType("character varying(32)").IsRequired();
        b.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes").HasColumnType("integer").IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        b.HasIndex(x => new { x.ProfileId, x.SlotIndex }).IsUnique().HasDatabaseName("uq_additional_photos_profile_slot");
        b.HasIndex(x => new { x.AccountId, x.Module, x.SlotIndex }).HasDatabaseName("ix_additional_photos_account_module");
        b.HasOne<PrivateModuleProfile>().WithMany()
            .HasForeignKey(x => new { x.ProfileId, x.AccountId, x.Module })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.Module })
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_additional_photos_profiles");
    }
}
