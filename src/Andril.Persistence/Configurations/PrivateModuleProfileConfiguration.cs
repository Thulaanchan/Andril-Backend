using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class PrivateModuleProfileConfiguration : IEntityTypeConfiguration<PrivateModuleProfile>
{
    public void Configure(EntityTypeBuilder<PrivateModuleProfile> b)
    {
        b.ToTable("private_module_profiles", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_profile_module", "module IN ('learning', 'hobbies', 'social')");
            t.HasCheckConstraint("ck_profile_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_profile_timestamp", "isfinite(created_at_utc)");
        });
        b.HasKey(x => x.Id).HasName("pk_private_module_profiles");
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("text");
        b.Property(x => x.Bio).HasColumnName("bio").HasColumnType("text");
        b.Property(x => x.Visibility).HasColumnName("visibility").HasColumnType("text").HasDefaultValue("private");
        b.Property(x => x.CanonicalDisplayName).HasColumnName("canonical_display_name").HasColumnType("text").UseCollation("C")
            .HasComputedColumnSql(Migrations.Accounts.ProfileFieldsModel.CanonicalSql, stored: true);
        b.HasIndex(x => new { x.Module, x.CanonicalDisplayName }).IsUnique().HasDatabaseName("uq_profiles_module_name");
        b.HasIndex(x => new { x.AccountId, x.CanonicalDisplayName }).IsUnique().HasDatabaseName("uq_profiles_owner_name");
        b.ToTable("private_module_profiles", "andril_identity", t => {
            t.HasCheckConstraint("ck_profile_name_trimmed", Migrations.Accounts.ProfileFieldsModel.TrimSql);
            t.HasCheckConstraint("ck_profile_visibility", "visibility = 'private'");
        });
        b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid");
        b.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        b.HasIndex(x => new { x.AccountId, x.Module }).IsUnique().HasDatabaseName("uq_profiles_account_module");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_profiles_accounts");
    }
}
