
using Andril.Hobbies.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class GalleryMediaConfiguration
    : IEntityTypeConfiguration<GalleryMedia>
{
    public void Configure(
        EntityTypeBuilder<GalleryMedia> builder)
    {
        builder.ToTable("GalleryMedia", HobbiesDatabaseOptions.Schema);

        // Primary key
        builder.HasKey(media => media.Id);

        // Gallery ownership
        builder.Property(media => media.OwnerModuleProfileId)
            .IsRequired();

        // Module isolation metadata
        builder.Property(media => media.Module)
            .IsRequired();

        // Photo metadata
        builder.Property(media => media.Title)
            .IsRequired();

        builder.Property(media => media.Description)
            .IsRequired(false);

        builder.Property(media => media.Category)
            .IsRequired();

        builder.Property(media => media.MediaType)
            .IsRequired();

        // Private media storage reference
        builder.Property(media => media.StorageObjectKey)
            .IsRequired();

        // Privacy and deletion status
        builder.Property(media => media.IsPrivate)
            .IsRequired();

        builder.Property(media => media.IsDeleted)
            .IsRequired();

        // Record creation timestamp
        builder.Property(media => media.CreatedAtUtc)
            .IsRequired();

        // Optimize owner-scoped gallery queries
        builder.HasIndex(media => new
        {
            media.OwnerModuleProfileId,
            media.Module,
            media.IsDeleted,
            media.CreatedAtUtc
        });
    }
}
