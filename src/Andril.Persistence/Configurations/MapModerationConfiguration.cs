using Andril.Hobbies.Map.Contributions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

// Pending revision zero is represented by a contribution without a receipt.
// Revision one is an immutable receipt with a required audit entry.
internal sealed class MapModerationReceipt
{
    public Guid DecisionId { get; init; }
    public Guid ContributionId { get; init; }
    public Guid AttractionId { get; init; }
    public Guid ProfileId { get; init; }
    public Guid SessionId { get; init; }
    public Guid ReviewerSubject { get; init; }
    public MapContributionReviewAction Action { get; init; }
    public long ExpectedRevision { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
    public Guid? MediaId { get; init; }
}

internal sealed class MapModerationAudit
{
    public Guid DecisionId { get; init; }
}

internal sealed class MapModerationReceiptConfiguration : IEntityTypeConfiguration<MapModerationReceipt>
{
    public void Configure(EntityTypeBuilder<MapModerationReceipt> builder)
    {
        builder.ToTable("map_moderation_receipts", "andril_hobbies", table =>
        {
            table.HasCheckConstraint("ck_map_moderation_revision", "expected_revision = 0 AND revision = 1");
            table.HasCheckConstraint("ck_map_moderation_action", "(action = 1 AND media_id IS NOT NULL) OR (action = 2 AND media_id IS NULL)");
            table.HasCheckConstraint("ck_map_moderation_identity",
                "decision_id <> '00000000-0000-0000-0000-000000000000' AND reviewer_subject <> '00000000-0000-0000-0000-000000000000'");
        });
        builder.HasKey(x => x.DecisionId).HasName("pk_map_moderation_receipts");
        builder.Property(x => x.DecisionId).HasColumnName("decision_id").ValueGeneratedNever();
        builder.Property(x => x.ContributionId).HasColumnName("contribution_id");
        builder.Property(x => x.AttractionId).HasColumnName("attraction_id");
        builder.Property(x => x.ProfileId).HasColumnName("profile_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.ReviewerSubject).HasColumnName("reviewer_subject");
        builder.Property(x => x.Action).HasColumnName("action").HasConversion<int>();
        builder.Property(x => x.ExpectedRevision).HasColumnName("expected_revision");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.MediaId).HasColumnName("media_id");
        builder.HasIndex(x => x.ContributionId).IsUnique().HasDatabaseName("ux_map_moderation_contribution");
        builder.HasOne<MapContribution>().WithMany()
            .HasForeignKey(x => new { x.ContributionId, x.AttractionId, x.ProfileId, x.SessionId })
            .HasPrincipalKey(x => new { x.ContributionId, x.AttractionId, x.OwnerHobbiesModuleProfileId, x.CaptureSessionId })
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_moderation_scope");
        builder.HasOne<Repositories.Hobbies.MapPrivateMediaRow>().WithMany()
            .HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("fk_map_moderation_media");
    }
}

internal sealed class MapModerationAuditConfiguration : IEntityTypeConfiguration<MapModerationAudit>
{
    public void Configure(EntityTypeBuilder<MapModerationAudit> builder)
    {
        builder.ToTable("map_moderation_audit", "andril_hobbies");
        builder.HasKey(x => x.DecisionId).HasName("pk_map_moderation_audit");
        builder.Property(x => x.DecisionId).HasColumnName("decision_id").ValueGeneratedNever();
        builder.HasOne<MapModerationReceipt>().WithOne()
            .HasForeignKey<MapModerationAudit>(x => x.DecisionId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_moderation_audit_receipt");
    }
}
