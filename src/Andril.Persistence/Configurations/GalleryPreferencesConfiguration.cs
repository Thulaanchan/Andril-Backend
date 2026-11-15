
using Andril.Hobbies.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class GalleryPreferencesConfiguration
    : IEntityTypeConfiguration<GalleryPreferences>
{
    public void Configure(
        EntityTypeBuilder<GalleryPreferences> builder)
    {
        // One gallery preference record per Hobbies profile.
        builder.HasKey(
            preferences => preferences.OwnerModuleProfileId);

        // Gallery owner identifier.
        builder.Property(
            preferences => preferences.OwnerModuleProfileId)
            .IsRequired()
            .ValueGeneratedNever();

        // Optional selected cover media.
        // Null means no cover is selected.
        builder.Property(
            preferences => preferences.CoverMediaId)
            .IsRequired(false);
    }
}
