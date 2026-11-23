
using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// EF Core configuration for private, module-scoped
/// ANDRIL notification persistence.
///
/// Offline metadata configuration only.
/// Production database integration requires separate
/// M2 authorization and M6 security approval.
/// </summary>
internal sealed class NotificationPersistenceRecordConfiguration
    : IEntityTypeConfiguration<NotificationPersistenceRecord>
{
    public void Configure(
        EntityTypeBuilder<NotificationPersistenceRecord> builder)
    {
        // Explicit Notifications table and schema.
        builder.ToTable(
            "notifications",
            "andril_notifications");

        // Application-generated notification identity.
        builder.HasKey(record => record.Id)
            .HasName("pk_notifications");

        builder.Property(record => record.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        // Private recipient module-profile identifier.
        builder.Property(record =>
                record.RecipientModuleProfileId)
            .HasColumnName("recipient_module_profile_id")
            .HasColumnType("uuid")
            .IsRequired();

        // Canonical module identifier:
        // learning, hobbies, social.
        builder.Property(record => record.Module)
            .HasColumnName("module")
            .HasColumnType("character varying(16)")
            .IsRequired();

        // Notification classification and display content.
        builder.Property(record => record.Type)
            .HasColumnName("type")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(record => record.Title)
            .HasColumnName("title")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(record => record.Preview)
            .HasColumnName("preview")
            .HasColumnType("text")
            .IsRequired(false);

        // Notification lifecycle timestamps (UTC).
        builder.Property(record => record.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(record => record.ReadAtUtc)
            .HasColumnName("read_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired(false);

        // Computed property; never persisted.
        builder.Ignore(record => record.IsRead);

        // Existing module-profile query index.
        builder.HasIndex(record => new
        {
            record.RecipientModuleProfileId,
            record.Module,
            record.CreatedAtUtc,
            record.Id
        })
        .HasDatabaseName(
            "ix_notifications_profile_module_created_id");
    }
}
