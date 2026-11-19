
using Andril.Hobbies.Map.Contributions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Defines the relational database mapping for
/// server-managed Map contribution capture sessions.
/// </summary>
public sealed class MapContributionCaptureSessionConfiguration
    : IEntityTypeConfiguration<MapContributionCaptureSession>
{
    public void Configure(
        EntityTypeBuilder<MapContributionCaptureSession> builder)
    {
        // Table and database-level validation constraints.
        builder.ToTable(
            "map_contribution_capture_sessions",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_map_capture_session_id",
                    "session_id <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid");

                table.HasCheckConstraint(
                    "ck_map_capture_owner_id",
                    "owner_hobbies_module_profile_id <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid");

                table.HasCheckConstraint(
                    "ck_map_capture_attraction_id",
                    "attraction_id <> " +
                    "'00000000-0000-0000-0000-000000000000'::uuid");

                table.HasCheckConstraint(
                    "ck_map_capture_timestamp_order",
                    "completed_at_utc IS NULL OR " +
                    "completed_at_utc >= started_at_utc");
            });

        // Primary key.
        builder.HasKey(session => session.SessionId);

        builder.Property(session => session.SessionId)
            .HasColumnName("session_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();

        // Hobbies module profile ownership.
        builder.Property(
                session => session.OwnerHobbiesModuleProfileId)
            .HasColumnName(
                "owner_hobbies_module_profile_id")
            .HasColumnType("uuid")
            .IsRequired();

        // Canonical attraction reference.
        builder.Property(session => session.AttractionId)
            .HasColumnName("attraction_id")
            .HasColumnType("uuid")
            .IsRequired();

        // Server-managed capture start timestamp.
        builder.Property(session => session.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Null until the session is completed.
        builder.Property(session => session.CompletedAtUtc)
            .HasColumnName("completed_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        // Supports contributor and attraction queries.
        builder.HasIndex(session => new
        {
            session.OwnerHobbiesModuleProfileId,
            session.AttractionId
        })
            .HasDatabaseName(
                "ix_map_capture_owner_attraction");
    }
}
