using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class AccountDemographicsConfiguration : IEntityTypeConfiguration<AccountDemographics>
{
    public void Configure(EntityTypeBuilder<AccountDemographics> builder)
    {
        builder.ToTable("account_demographics", "andril_identity", table =>
        {
            table.HasCheckConstraint("ck_account_demographics_dob", "isfinite(date_of_birth) AND date_of_birth >= DATE '0001-01-01' AND date_of_birth <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date");
            table.HasCheckConstraint("ck_account_demographics_gender", "char_length(btrim(gender)) > 0 AND gender !~ '^[[:space:]]*$' AND gender !~ '[[:cntrl:]]'");
            table.HasCheckConstraint("ck_account_demographics_timestamps", "updated_at_utc >= created_at_utc");
        });
        builder.HasKey(x => x.AccountId).HasName("pk_account_demographics");
        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.DateOfBirth).HasColumnName("date_of_birth").HasColumnType("date").IsRequired();
        builder.Property(x => x.Gender).HasColumnName("gender").HasColumnType("character varying(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasOne<Account>().WithOne().HasForeignKey<AccountDemographics>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_account_demographics_accounts");
    }
}
