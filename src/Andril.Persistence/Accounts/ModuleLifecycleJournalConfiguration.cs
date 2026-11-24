using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Accounts;

internal sealed class ModuleLifecycleReceiptRow
{
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = null!;
    public Guid RequestKey { get; private set; }
    public string Operation { get; private set; } = null!;
    public long? ExpectedRevision { get; private set; }
    public string IntentHash { get; private set; } = null!;
    public long Revision { get; private set; }
    public DateTimeOffset StateChangedAtUtc { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
}

internal sealed class ModuleLifecycleAuditRow
{
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = null!;
    public Guid RequestKey { get; private set; }
    public long Revision { get; private set; }
    public string Operation { get; private set; } = null!;
    public string? PreviousState { get; private set; }
    public Guid CorrelationId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
}

internal sealed class ModuleLifecycleReceiptConfiguration : IEntityTypeConfiguration<ModuleLifecycleReceiptRow>
{
    public void Configure(EntityTypeBuilder<ModuleLifecycleReceiptRow> b)
    {
        b.ToTable("account_module_lifecycle_receipts", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_lifecycle_receipt_intent", "intent_hash COLLATE \"C\" ~ '^[0-9A-F]{64}$'");
            t.HasCheckConstraint("ck_lifecycle_receipt_module", "module IN ('learning', 'hobbies', 'social')");
            t.HasCheckConstraint("ck_lifecycle_receipt_revision", Andril.Persistence.Migrations.Accounts.AddControlledDeactivationOutcomes.RevisionCheck);
            t.HasCheckConstraint("ck_lifecycle_receipt_time", "isfinite(recorded_at_utc) AND isfinite(state_changed_at_utc) AND recorded_at_utc >= state_changed_at_utc");
            t.HasCheckConstraint("ck_lifecycle_receipt_key", "request_key <> '00000000-0000-0000-0000-000000000000'");
        });
        b.HasKey(x => new { x.AccountId, x.Module, x.RequestKey });
        b.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.RequestKey).HasColumnName("request_key").ValueGeneratedNever();
        b.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(10).UseCollation("C");
        b.Property(x => x.ExpectedRevision).HasColumnName("expected_revision");
        b.Property(x => x.IntentHash).HasColumnName("intent_hash").HasMaxLength(64).UseCollation("C");
        b.Property(x => x.Revision).HasColumnName("revision");
        b.Property(x => x.StateChangedAtUtc).HasColumnName("state_changed_at_utc");
        b.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc").HasDefaultValueSql("statement_timestamp()");
        b.Property<string>("MembershipModule").HasColumnName("membership_module").HasMaxLength(16).UseCollation("C")
            .HasComputedColumnSql(Andril.Persistence.Migrations.Accounts.AddControlledDeactivationOutcomes.MembershipModuleSql, stored: true);
        b.HasIndex("AccountId", "MembershipModule").HasDatabaseName("ix_lifecycle_receipts_result_membership");
        b.HasOne<ModuleLifecycleRow>().WithMany().HasForeignKey("AccountId", "MembershipModule")
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_lifecycle_receipts_result_membership");
        b.HasOne<Andril.Common.Accounts.Account>().WithMany().HasForeignKey(x => x.AccountId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_lifecycle_receipts_accounts");
    }
}

internal sealed class ModuleLifecycleAuditConfiguration : IEntityTypeConfiguration<ModuleLifecycleAuditRow>
{
    public void Configure(EntityTypeBuilder<ModuleLifecycleAuditRow> b)
    {
        b.ToTable("account_module_lifecycle_audit", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_lifecycle_audit_transition", "revision > 0 AND ((operation = 'activate' AND previous_state IS NULL AND revision = 1) OR (operation = 'reactivate' AND previous_state IS NOT NULL AND previous_state = 'inactive' AND revision > 1) OR (operation = 'deactivate' AND previous_state IS NOT NULL AND previous_state = 'active' AND revision > 1))");
            t.HasCheckConstraint("ck_lifecycle_audit_time", "isfinite(recorded_at_utc)");
            t.HasCheckConstraint("ck_lifecycle_audit_correlation", "correlation_id <> '00000000-0000-0000-0000-000000000000'");
        });
        b.HasKey(x => new { x.AccountId, x.Module, x.Revision });
        b.HasIndex(x => new { x.AccountId, x.Module, x.RequestKey }).IsUnique();
        b.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(16).UseCollation("C");
        b.Property(x => x.RequestKey).HasColumnName("request_key");
        b.Property(x => x.Revision).HasColumnName("revision").ValueGeneratedNever();
        b.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(10).UseCollation("C");
        b.Property(x => x.PreviousState).HasColumnName("previous_state").HasMaxLength(8).UseCollation("C");
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        b.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc").HasDefaultValueSql("statement_timestamp()");
        b.HasOne<ModuleLifecycleReceiptRow>().WithMany().HasForeignKey(x => new { x.AccountId, x.Module, x.RequestKey }).OnDelete(DeleteBehavior.Restrict);
    }
}
