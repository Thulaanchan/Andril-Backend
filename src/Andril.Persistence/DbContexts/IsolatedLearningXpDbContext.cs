using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

internal sealed class IsolatedLearningXpDbContext(DbContextOptions<IsolatedLearningXpDbContext> options) : DbContext(options)
{
    internal const string Schema = "andril_learning_xp_isolated";
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema); var row = model.Entity<LearningXpRow>();
        row.ToTable("xp_credits", t =>
        {
            t.HasCheckConstraint("ck_learning_xp_scope", "module='learning' AND id <> '00000000-0000-0000-0000-000000000000'::uuid AND account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND source_event_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_xp_credit", "((entry_kind='Credit' AND amount>0 AND original_credit_id IS NULL) OR (entry_kind IN ('Reversal','Correction') AND amount<>0 AND original_credit_id IS NOT NULL AND length(btrim(audit_reason))>0)) AND length(btrim(source_kind)) > 0 AND length(btrim(policy_revision)) > 0 AND length(btrim(evidence_revision)) > 0");
        });
        row.HasKey(x => x.Id); row.Property(x => x.Id).ValueGeneratedNever();
        row.HasAlternateKey(x=>new{x.Id,x.AccountId,x.ProfileId,x.Module});
        row.HasOne<LearningXpRow>().WithMany().HasForeignKey(x=>new{x.OriginalCreditId,x.AccountId,x.ProfileId,x.Module})
            .HasPrincipalKey(x=>new{x.Id,x.AccountId,x.ProfileId,x.Module}).OnDelete(DeleteBehavior.Restrict);
        row.Property(x => x.Module).HasMaxLength(16); row.Property(x => x.SourceKind).HasMaxLength(128);
        row.HasIndex(x => new { x.ProfileId, x.SourceKind, x.SourceEventId }).IsUnique();
        row.HasIndex(x => new { x.AccountId, x.ProfileId, x.Id });
        row.HasIndex(x => new { x.ProfileId,x.OriginalCreditId }).IsUnique().HasFilter("entry_kind='Reversal'");
        foreach (var p in row.Metadata.GetProperties())
            p.SetColumnName(string.Concat(p.Name.Select((c,i) => i > 0 && char.IsUpper(c) ? "_"+char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
        var policy=model.Entity<LearningXpPolicyRow>();
        policy.ToTable("xp_policy_versions",t=>t.HasCheckConstraint("ck_xp_policy_scope","module='learning' AND length(btrim(revision))>0 AND jsonb_typeof(configuration)='object'"));
        policy.HasKey(x=>new{x.AccountId,x.ProfileId,x.Revision});policy.Property(x=>x.Configuration).HasColumnType("jsonb");
        foreach(var p in policy.Metadata.GetProperties())p.SetColumnName(string.Concat(p.Name.Select((c,i)=>i>0&&char.IsUpper(c)?"_"+char.ToLowerInvariant(c):char.ToLowerInvariant(c).ToString())));
    }
}
internal sealed class LearningXpPolicyRow
{
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public string Module { get; set; }="learning";
    public string Revision { get; set; }="";
    public string Configuration { get; set; }="{}";
    public override string ToString()=>"Immutable Learning XP policy version [values omitted]";
}
internal sealed class LearningXpRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public string Module { get; set; } = "learning";
    public string SourceKind { get; set; } = "";
    public Guid SourceEventId { get; set; }
    public long Amount { get; set; }
    public string EntryKind { get; set; } = "Credit";
    public Guid? OriginalCreditId { get; set; }
    public string? AuditReason { get; set; }
    public string PolicyRevision { get; set; } = "";
    public string EvidenceRevision { get; set; } = "";
    public DateTimeOffset CreditedAtUtc { get; set; }
    public override string ToString() => "Private immutable XP credit [content omitted]";
}
