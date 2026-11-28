using Andril.Common.Accounts.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class ProfileLocationConfiguration : IEntityTypeConfiguration<ProfileLocationRecord>
{
    public void Configure(EntityTypeBuilder<ProfileLocationRecord> b)
    {
        b.ToTable("account_profile_locations", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_profile_location_profile_id", "profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_profile_location_module", "module IN ('learning', 'hobbies', 'social')");
            t.HasCheckConstraint("ck_profile_location_city_nonempty", "length(trim(city)) > 0");
            t.HasCheckConstraint("ck_profile_location_timestamps", "updated_at_utc >= created_at_utc");
        });

        b.HasKey(x => x.ProfileId).HasName("pk_account_profile_locations");

        b.Property(x => x.ProfileId).HasColumnName("profile_id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").IsRequired();
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(16).HasColumnType("character varying(16)").IsRequired();
        b.Property(x => x.City).HasColumnName("city").HasMaxLength(100).HasColumnType("character varying(100)").IsRequired();
        b.Property(x => x.Region).HasColumnName("region").HasMaxLength(100).HasColumnType("character varying(100)");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        b.HasIndex(x => new { x.AccountId, x.Module }).IsUnique().HasDatabaseName("uq_profile_locations_account_module");
        b.HasOne<PrivateModuleProfile>().WithOne()
            .HasForeignKey<ProfileLocationRecord>(x => new { x.ProfileId, x.AccountId, x.Module })
            .HasPrincipalKey<PrivateModuleProfile>(x => new { x.Id, x.AccountId, x.Module })
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_profile_locations_profiles");
    }
}
