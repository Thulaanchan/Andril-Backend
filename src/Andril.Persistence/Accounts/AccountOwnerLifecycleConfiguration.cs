using Andril.Common.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Accounts;

internal sealed class AccountOwnerStateRow
{
    public Guid AccountId { get; set; }
    public string State { get; set; } = null!;
    public long Revision { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}

internal sealed class AccountOwnerReceiptRow
{
    public Guid AccountId { get; set; }
    public Guid RequestKey { get; set; }
    public string IntentHash { get; set; } = null!;
    public string State { get; set; } = null!;
    public long Revision { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}

internal sealed class AccountOwnerAuditRow
{
    public Guid AccountId { get; set; }
    public Guid RequestKey { get; set; }
    public string Operation { get; set; } = null!;
    public string PreviousState { get; set; } = null!;
    public string State { get; set; } = null!;
    public long Revision { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}

internal sealed class AccountOwnerStateConfiguration : IEntityTypeConfiguration<AccountOwnerStateRow>
{
    public void Configure(EntityTypeBuilder<AccountOwnerStateRow> b)
    {
        b.ToTable("account_owner_states", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_owner_state", "state IN ('Active','Suspended','Deactivated','Deleted')");
            t.HasCheckConstraint("ck_owner_revision", "revision > 0");
            t.HasCheckConstraint("ck_owner_time", "isfinite(changed_at_utc)");
        });
        b.HasKey(x => x.AccountId);
        b.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken();
        b.Property(x => x.ChangedAtUtc).HasColumnName("changed_at_utc");
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccountOwnerReceiptConfiguration : IEntityTypeConfiguration<AccountOwnerReceiptRow>
{
    public void Configure(EntityTypeBuilder<AccountOwnerReceiptRow> b)
    {
        b.ToTable("account_owner_receipts", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_owner_receipt", "request_key <> '00000000-0000-0000-0000-000000000000'::uuid AND revision > 1 AND state IN ('Active','Deactivated','Deleted') AND intent_hash COLLATE \"C\" ~ '^[0-9A-F]{64}$' AND isfinite(changed_at_utc)");
        });
        b.HasKey(x => new { x.AccountId, x.RequestKey });
        b.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        b.Property(x => x.RequestKey).HasColumnName("request_key").ValueGeneratedNever();
        b.Property(x => x.IntentHash).HasColumnName("intent_hash").HasMaxLength(64).UseCollation("C");
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.Revision).HasColumnName("revision");
        b.Property(x => x.ChangedAtUtc).HasColumnName("changed_at_utc");
        b.HasOne<AccountOwnerStateRow>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AccountOwnerAuditConfiguration : IEntityTypeConfiguration<AccountOwnerAuditRow>
{
    public void Configure(EntityTypeBuilder<AccountOwnerAuditRow> b)
    {
        b.ToTable("account_owner_audit", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_owner_audit", "revision > 1 AND isfinite(changed_at_utc) AND ((operation = 'Deactivate' AND previous_state = 'Active' AND state = 'Deactivated') OR (operation = 'Reactivate' AND previous_state = 'Deactivated' AND state = 'Active') OR (operation = 'Delete' AND previous_state IN ('Active','Deactivated') AND state = 'Deleted'))");
        });
        b.HasKey(x => new { x.AccountId, x.Revision });
        b.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        b.Property(x => x.RequestKey).HasColumnName("request_key");
        b.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.PreviousState).HasColumnName("previous_state").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.Revision).HasColumnName("revision");
        b.Property(x => x.ChangedAtUtc).HasColumnName("changed_at_utc");
        b.HasOne<AccountOwnerReceiptRow>().WithMany().HasForeignKey(x => new { x.AccountId, x.RequestKey }).OnDelete(DeleteBehavior.Restrict);
    }
}
