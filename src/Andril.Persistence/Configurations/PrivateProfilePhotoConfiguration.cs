using Andril.Common.Accounts.Profiles;
using Andril.Common.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class PrivateProfilePhotoConfiguration : IEntityTypeConfiguration<PrivateProfilePhoto>
{
    public void Configure(EntityTypeBuilder<PrivateProfilePhoto> b)
    {
        b.ToTable("private_profile_photos", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_profile_photo_ids", "id <> '00000000-0000-0000-0000-000000000000' AND request_id <> '00000000-0000-0000-0000-000000000000' AND id <> request_id");
            t.HasCheckConstraint("ck_profile_photo_module", "module IN ('learning','hobbies','social')");
            t.HasCheckConstraint("ck_profile_photo_key", "object_key = module || '/' || profile_id::text || '/' || id::text || '.jpg'");
            t.HasCheckConstraint("ck_profile_photo_hashes", "original_content_hash COLLATE \"C\" ~ '^[0-9a-f]{64}$' AND stored_content_hash COLLATE \"C\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_profile_photo_media", "media_type = 'image/jpeg' AND file_size_bytes BETWEEN 1 AND 5242880 AND width BETWEEN 1 AND 1024 AND height BETWEEN 1 AND 1024");
            t.HasCheckConstraint("ck_profile_photo_state", """
                (state = 'PendingStorage' AND reason = 'None' AND NOT is_current
                  AND NOT cleanup_pending AND superseded_at_utc IS NULL
                  AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)
                OR (state = 'Stored' AND reason = 'None'
                  AND is_current = (superseded_at_utc IS NULL) AND (NOT is_current OR NOT cleanup_pending)
                  AND lease_token IS NULL AND lease_until_utc IS NULL)
                OR (state = 'Failed' AND reason IN ('StorageFailure','UploadAbandoned','IntegrityFailure')
                  AND NOT is_current AND superseded_at_utc IS NULL
                  AND lease_token IS NULL AND lease_until_utc IS NULL)
                """);
            t.HasCheckConstraint("ck_profile_photo_cleanup", "cleanup_pending = (next_cleanup_attempt_at_utc IS NOT NULL) AND (next_cleanup_attempt_at_utc IS NULL OR (isfinite(next_cleanup_attempt_at_utc) AND next_cleanup_attempt_at_utc >= created_at_utc)) AND ((cleanup_claim_id IS NULL AND cleanup_claim_expires_at_utc IS NULL) OR (cleanup_pending AND cleanup_claim_id IS NOT NULL AND cleanup_claim_id <> '00000000-0000-0000-0000-000000000000' AND cleanup_claim_expires_at_utc IS NOT NULL AND isfinite(cleanup_claim_expires_at_utc) AND cleanup_claim_expires_at_utc > updated_at_utc))");
            t.HasCheckConstraint("ck_profile_photo_late_write", "NOT late_write_risk OR state = 'PendingStorage' OR is_current OR cleanup_pending");
            t.HasCheckConstraint("ck_profile_photo_times", """
                isfinite(created_at_utc) AND isfinite(updated_at_utc) AND updated_at_utc >= created_at_utc
                AND (superseded_at_utc IS NULL OR (isfinite(superseded_at_utc) AND superseded_at_utc BETWEEN created_at_utc AND updated_at_utc))
                AND (lease_until_utc IS NULL OR (isfinite(lease_until_utc) AND lease_until_utc > updated_at_utc))
                AND (lease_token IS NULL OR lease_token <> '00000000-0000-0000-0000-000000000000')
                """);
        });

        b.HasKey(x => x.Id).HasName("pk_private_profile_photos");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.ProfileId).HasColumnName("profile_id");
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
        b.Property(x => x.RequestId).HasColumnName("request_id");
        b.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(88).IsRequired();
        b.Property(x => x.OriginalContentHash).HasColumnName("original_content_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.StoredContentHash).HasColumnName("stored_content_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.MediaType).HasColumnName("media_type").HasMaxLength(16).IsRequired();
        b.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        b.Property(x => x.Width).HasColumnName("width");
        b.Property(x => x.Height).HasColumnName("height");
        b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Reason).HasColumnName("reason").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.IsCurrent).HasColumnName("is_current");
        b.Property(x => x.CleanupPending).HasColumnName("cleanup_pending");
        b.Property(x => x.CleanupClaimId).HasColumnName("cleanup_claim_id").IsConcurrencyToken();
        b.Property(x => x.CleanupClaimExpiresAtUtc).HasColumnName("cleanup_claim_expires_at_utc");
        b.Property(x => x.NextCleanupAttemptAtUtc).HasColumnName("next_cleanup_attempt_at_utc");
        b.Property(x => x.LateWriteRisk).HasColumnName("late_write_risk");
        b.Property(x => x.LeaseToken).HasColumnName("lease_token").IsConcurrencyToken();
        b.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        b.Property(x => x.SupersededAtUtc).HasColumnName("superseded_at_utc");
        b.HasIndex(x => new { x.ProfileId, x.Module, x.RequestId }).IsUnique().HasDatabaseName("uq_profile_photo_request");
        b.HasIndex(x => x.ObjectKey).IsUnique().HasDatabaseName("uq_profile_photo_object");
        b.HasIndex(x => x.ProfileId, "CurrentPhoto").IsUnique().HasFilter("is_current").HasDatabaseName("uq_profile_photo_current");
        b.HasIndex(x => x.ProfileId, "PendingPhoto").IsUnique().HasFilter("state = 'PendingStorage'").HasDatabaseName("uq_profile_photo_pending");
        b.HasIndex(x => new { x.ProfileId, x.Module, x.NextCleanupAttemptAtUtc, x.Id }).HasFilter("cleanup_pending").HasDatabaseName("ix_profile_photo_cleanup");
        b.HasOne<PrivateModuleProfile>().WithMany().HasForeignKey(x => new { x.ProfileId, x.Module })
            .HasPrincipalKey(x => new { x.Id, x.Module }).HasConstraintName("fk_profile_photo_profile_module")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
