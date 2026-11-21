using Andril.Hobbies.Map.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Defines PostgreSQL mappings and integrity constraints for canonical Map attractions.
/// </summary>
public sealed class MapAttractionConfiguration : IEntityTypeConfiguration<MapAttraction>
{
    public void Configure(EntityTypeBuilder<MapAttraction> builder)
    {
        builder.ToTable("map_attractions", HobbiesDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_map_attraction_id",
                "attraction_id <> '00000000-0000-0000-0000-000000000000'::uuid");

            // PostgreSQL whitespace classification differs from .NET Unicode whitespace.
            // Domain validation remains necessary for backend writes.
            table.HasCheckConstraint(
                "ck_map_attraction_name",
                "char_length(btrim(name)) > 0 AND name !~ '^[[:space:]]*$'");

            table.HasCheckConstraint(
                "ck_map_attraction_latitude",
                "latitude >= -90 AND latitude <= 90 AND latitude <> 'NaN'::double precision");

            table.HasCheckConstraint(
                "ck_map_attraction_longitude",
                "longitude >= -180 AND longitude <= 180 AND longitude <> 'NaN'::double precision");
        });

        builder.HasKey(attraction => attraction.AttractionId)
            .HasName("pk_map_attractions");

        // Explicitly map get-only properties so EF can bind the private constructor
        // parameters attractionId, name, latitude and longitude to their properties.
        builder.Property(attraction => attraction.AttractionId)
            .HasColumnName("attraction_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(attraction => attraction.Name)
            .HasColumnName("name")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(attraction => attraction.Latitude)
            .HasColumnName("latitude")
            .HasColumnType("double precision")
            .IsRequired();

        builder.Property(attraction => attraction.Longitude)
            .HasColumnName("longitude")
            .HasColumnType("double precision")
            .IsRequired();
    }
}
