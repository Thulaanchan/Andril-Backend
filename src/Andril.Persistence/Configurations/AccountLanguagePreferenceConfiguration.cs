using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class AccountLanguagePreferenceConfiguration : IEntityTypeConfiguration<AccountLanguagePreference>
{
    public void Configure(EntityTypeBuilder<AccountLanguagePreference> builder)
    {
        builder.ToTable("account_language_preferences", "andril_identity", table =>
        {
            table.HasCheckConstraint("ck_account_language_supported", "language IN ('en', 'ta', 'si')");
            table.HasCheckConstraint("ck_account_language_timestamps", "updated_at_utc >= created_at_utc");
        });
        builder.HasKey(x => x.AccountId).HasName("pk_account_language_preferences");
        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.Language).HasColumnName("language").HasMaxLength(2).HasColumnType("character varying(2)").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<Account>().WithOne().HasForeignKey<AccountLanguagePreference>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_account_language_preferences_accounts");
    }
}
