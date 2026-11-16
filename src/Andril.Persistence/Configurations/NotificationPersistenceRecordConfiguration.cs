
using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Proposed EF Core configuration for module-scoped
/// ANDRIL notification persistence.
///
/// Not registered with a DbContext yet.
/// No database migration is applied by this class.
///
/// Production use requires M2 identity authorization,
/// approved database schema, and security review.
/// </summary>
internal sealed class NotificationPersistenceRecordConfiguration
    : IEntityTypeConfiguration<NotificationPersistenceRecord>
{
    public void Configure(
        EntityTypeBuilder<NotificationPersistenceRecord> builder)
    {
        // Application-generated notification identity.
        builder.HasKey(record => record.Id);

        builder.Property(record => record.Id)
            .ValueGeneratedNever();

        // Private recipient profile identifier.
        builder.Property(record =>
                record.RecipientModuleProfileId)
            .IsRequired();

        // Learning, Hobbies, or Social.
        builder.Property(record => record.Module)
            .IsRequired();

        // Notification classification and display content.
        builder.Property(record => record.Type)
            .IsRequired();

        builder.Property(record => record.Title)
            .IsRequired();

        builder.Property(record => record.Preview)
            .IsRequired(false);

        // Notification lifecycle timestamps.
        builder.Property(record => record.CreatedAtUtc)
            .IsRequired();

        builder.Property(record => record.ReadAtUtc)
            .IsRequired(false);

        // Calculated from ReadAtUtc; not a storage column.
        builder.Ignore(record => record.IsRead);

        // Proposed index for module-profile-scoped queries.
        builder.HasIndex(record => new
        {
            record.RecipientModuleProfileId,
            record.Module,
            record.CreatedAtUtc,
            record.Id
        });
    }
}
