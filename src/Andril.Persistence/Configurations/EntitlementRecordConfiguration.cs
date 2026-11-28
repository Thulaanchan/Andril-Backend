using Andril.Common.Accounts;
using Andril.Economy.Premium;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class EntitlementRecordConfiguration : IEntityTypeConfiguration<EntitlementRecord>
{
    public void Configure(EntityTypeBuilder<EntitlementRecord> builder)
    {
        builder.ToTable("premium_entitlements", EconomyDatabaseOptions.Schema, table =>
        {
            table.ExcludeFromMigrations();
            table.HasCheckConstraint("ck_premium_entitlements_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_premium_entitlements_timestamps", "updated_at_utc >= created_at_utc");
        });

        builder.HasKey(x => x.AccountId).HasName("pk_premium_entitlements");

        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.Tier).HasColumnName("tier").HasConversion<int>().HasColumnType("integer").IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(32).HasColumnType("character varying(32)").IsRequired();
        builder.Property(x => x.ExternalSubscriptionId).HasColumnName("external_subscription_id").HasMaxLength(128).HasColumnType("character varying(128)");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasOne<Account>().WithOne().HasForeignKey<EntitlementRecord>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_premium_entitlements_accounts");
    }
}
