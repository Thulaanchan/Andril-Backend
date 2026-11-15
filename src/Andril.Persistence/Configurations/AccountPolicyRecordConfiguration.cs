using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class AccountPolicyRecordConfiguration : IEntityTypeConfiguration<AccountPolicyRecord>
{
    public void Configure(EntityTypeBuilder<AccountPolicyRecord> b)
    {
        b.ToTable("account_policy_records", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_account_policy_action", "action IN ('terms_agreed', 'privacy_acknowledged')");
            t.HasCheckConstraint("ck_account_policy_version", "char_length(btrim(version)) > 0 AND version = btrim(version) AND version !~ '[[:cntrl:]]'");
            t.HasCheckConstraint("ck_account_policy_timestamp", "isfinite(recorded_at_utc)");
        });
        b.HasKey(x => new { x.AccountId, x.Action, x.Version }).HasName("pk_account_policy_records");
        b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.Action).HasColumnName("action").HasColumnType("character varying(32)").HasMaxLength(32);
        b.Property(x => x.Version).HasColumnName("version").HasColumnType("character varying(200)").HasMaxLength(200);
        b.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc").HasColumnType("timestamp with time zone");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_account_policy_records_accounts");
    }
}
