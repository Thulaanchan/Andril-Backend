using Andril.Common.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Accounts;

// Offline mapping only. AccountsDbContext and its migration snapshot exclude this type.
internal sealed class ModuleLifecycleRow
{
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = null!;
    public string State { get; private set; } = null!;
    public long Revision { get; private set; }
    public DateTimeOffset FirstActivatedAtUtc { get; private set; }
    public DateTimeOffset StateChangedAtUtc { get; private set; }
}

internal sealed class ModuleLifecycleConfiguration : IEntityTypeConfiguration<ModuleLifecycleRow>
{
    public void Configure(EntityTypeBuilder<ModuleLifecycleRow> builder)
    {
        builder.ToTable("account_module_lifecycles", "andril_identity", table =>
        {
            table.HasCheckConstraint("ck_account_module_lifecycles_module", "module IN ('learning', 'hobbies', 'social')");
            table.HasCheckConstraint("ck_account_module_lifecycles_state", "state IN ('active', 'inactive')");
            table.HasCheckConstraint("ck_account_module_lifecycles_revision", "revision > 0");
            table.HasCheckConstraint("ck_account_module_lifecycles_timestamps",
                "isfinite(first_activated_at_utc) AND isfinite(state_changed_at_utc) AND state_changed_at_utc >= first_activated_at_utc");
        });

        builder.HasKey(row => new { row.AccountId, row.Module }).HasName("pk_account_module_lifecycles");
        builder.Property(row => row.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(row => row.Module).HasColumnName("module").HasMaxLength(16)
            .UseCollation("C").IsRequired().ValueGeneratedNever();
        builder.Property(row => row.State).HasColumnName("state").HasMaxLength(8).UseCollation("C").IsRequired();
        builder.Property(row => row.Revision).HasColumnName("revision").HasColumnType("bigint").IsConcurrencyToken();

        // Defaults share the insert statement's timestamp. A future approved writer
        // must explicitly set state_changed_at_utc from the database on a real change.
        builder.Property(row => row.FirstActivatedAtUtc).HasColumnName("first_activated_at_utc")
            .HasColumnType("timestamp with time zone").HasDefaultValueSql("statement_timestamp()");
        builder.Property(row => row.StateChangedAtUtc).HasColumnName("state_changed_at_utc")
            .HasColumnType("timestamp with time zone").HasDefaultValueSql("statement_timestamp()");

        builder.HasOne<Account>().WithMany().HasForeignKey(row => row.AccountId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_account_module_lifecycles_accounts");
    }
}
