using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

internal sealed class MapPrivateMediaConfiguration : IEntityTypeConfiguration<MapPrivateMediaRow>
{
    public void Configure(EntityTypeBuilder<MapPrivateMediaRow> builder)
    {
        builder.ToTable("map_private_media", "andril_hobbies", table =>
        {
            table.HasCheckConstraint("ck_map_media_upload_window",
                "upload_expires_at > reserved_at AND upload_expires_at <= reserved_at + interval '120 seconds'");
            table.HasCheckConstraint("ck_map_media_evidence_state", """
                (finalized_at IS NULL AND private_reference IS NULL AND version IS NULL AND sha256 IS NULL AND captured_at IS NULL)
                OR (finalized_at IS NOT NULL AND private_reference IS NOT NULL AND length(private_reference) > 0
                    AND version IS NOT NULL AND length(version) > 0 AND sha256 IS NOT NULL
                    AND sha256 ~ '^[0-9A-Fa-f]{64}$' AND captured_at IS NOT NULL
                    AND captured_at <= finalized_at AND cleanup_claim IS NULL)
                """);
            table.HasCheckConstraint("ck_map_media_cleanup_state",
                "cleaned_at IS NULL OR (cleanup_claim IS NOT NULL AND finalized_at IS NULL)");
        });
        builder.HasKey(x => x.MediaId).HasName("pk_map_private_media");
        builder.Property(x => x.MediaId).HasColumnName("media_id").ValueGeneratedNever();
        builder.Property(x => x.SubjectId).HasColumnName("subject_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id");
        builder.Property(x => x.AttractionId).HasColumnName("attraction_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.Challenge).HasColumnName("capture_challenge");
        builder.Property(x => x.ReservedAt).HasColumnName("reserved_at");
        builder.Property(x => x.UploadExpiresAt).HasColumnName("upload_expires_at");
        builder.Property(x => x.PrivateReference).HasColumnName("private_reference");
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.Sha256).HasColumnName("sha256");
        builder.Property(x => x.CapturedAt).HasColumnName("captured_at");
        builder.Property(x => x.FinalizedAt).HasColumnName("finalized_at");
        builder.Property(x => x.CleanupClaim).HasColumnName("cleanup_claim");
        builder.Property(x => x.CleanedAt).HasColumnName("cleaned_at");
        builder.HasIndex(x => x.SessionId).IsUnique().HasDatabaseName("ux_map_private_media_session");
        builder.HasIndex(x => x.Challenge).IsUnique().HasDatabaseName("ux_map_private_media_challenge");
        builder.HasIndex(x => x.PrivateReference).IsUnique().HasDatabaseName("ux_map_private_media_reference");
        builder.HasOne<MapContributionCaptureSession>().WithMany()
            .HasForeignKey(x => new { x.SessionId, x.ProfileId, x.AttractionId })
            .HasPrincipalKey(x => new { x.SessionId, x.OwnerHobbiesModuleProfileId, x.AttractionId })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("fk_map_private_media_session_scope");
    }
}
