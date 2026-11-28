using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>Independent Learning XP ledger model; no earning policy publication or runtime writer.</summary>
public sealed class LearningXpReadDbContext(DbContextOptions<LearningXpReadDbContext> options):DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)=>optionsBuilder.UseNpgsql(
        options=>options.MigrationsHistoryTable("__EFMigrationsHistory_LearningXpRead","andril_learning"));
    protected override void OnModelCreating(ModelBuilder model)=>Configure(model);
    internal static void Configure(ModelBuilder model)=>LearningXpReadModelV1.Configure(model);
}

// Versioned migration model: later model revisions must leave V1 intact.
internal static class LearningXpReadModelV1
{
    internal const string ScopeCheck="module='learning' AND id<>'00000000-0000-0000-0000-000000000000'::uuid AND account_id<>'00000000-0000-0000-0000-000000000000'::uuid AND profile_id<>'00000000-0000-0000-0000-000000000000'::uuid AND source_event_id<>'00000000-0000-0000-0000-000000000000'::uuid";
    internal const string CreditCheck="COALESCE((((entry_kind='Credit' AND amount>0 AND original_credit_id IS NULL) OR (entry_kind IN ('Reversal','Correction') AND amount<>0 AND original_credit_id IS NOT NULL AND length(btrim(audit_reason))>0)) AND length(btrim(source_kind))>0 AND length(btrim(policy_revision))>0 AND length(btrim(evidence_revision))>0),false)";
    internal static void Configure(ModelBuilder model)
    {
        model.HasDefaultSchema("andril_learning");var row=model.Entity<LearningXpRow>();
        row.ToTable("xp_credits","andril_learning",table=>{table.HasCheckConstraint("ck_learning_xp_scope",ScopeCheck);table.HasCheckConstraint("ck_learning_xp_credit",CreditCheck);});
        row.HasKey(x=>x.Id).HasName("pk_learning_xp_credits");
        row.HasAlternateKey(x=>new{x.Id,x.AccountId,x.ProfileId,x.Module}).HasName("ak_learning_xp_credit_scope");
        row.HasOne<LearningXpRow>().WithMany().HasForeignKey(x=>new{x.OriginalCreditId,x.AccountId,x.ProfileId,x.Module})
            .HasPrincipalKey(x=>new{x.Id,x.AccountId,x.ProfileId,x.Module}).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_learning_xp_adjustment_credit_scope");
        row.Property(x=>x.Id).HasColumnName("id").ValueGeneratedNever();row.Property(x=>x.AccountId).HasColumnName("account_id");
        row.Property(x=>x.ProfileId).HasColumnName("profile_id");row.Property(x=>x.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
        row.Property(x=>x.SourceKind).HasColumnName("source_kind").HasMaxLength(128).IsRequired();row.Property(x=>x.SourceEventId).HasColumnName("source_event_id");
        row.Property(x=>x.Amount).HasColumnName("amount");row.Property(x=>x.EntryKind).HasColumnName("entry_kind").IsRequired();
        row.Property(x=>x.OriginalCreditId).HasColumnName("original_credit_id");row.Property(x=>x.AuditReason).HasColumnName("audit_reason").IsRequired(false);
        row.Property(x=>x.PolicyRevision).HasColumnName("policy_revision").IsRequired();row.Property(x=>x.EvidenceRevision).HasColumnName("evidence_revision").IsRequired();
        row.Property(x=>x.CreditedAtUtc).HasColumnName("credited_at_utc").HasColumnType("timestamp with time zone");
        row.HasIndex(x=>new{x.ProfileId,x.SourceKind,x.SourceEventId}).IsUnique().HasDatabaseName("ux_learning_xp_source_event");
        row.HasIndex(x=>new{x.AccountId,x.ProfileId,x.Id}).HasDatabaseName("ix_learning_xp_owner_history");
        row.HasIndex(x=>new{x.OriginalCreditId,x.AccountId,x.ProfileId,x.Module}).HasDatabaseName("ix_learning_xp_adjustment_origin");
        row.HasIndex(x=>new{x.ProfileId,x.OriginalCreditId}).IsUnique().HasFilter("entry_kind='Reversal'").HasDatabaseName("ux_learning_xp_reversal");
    }
}
