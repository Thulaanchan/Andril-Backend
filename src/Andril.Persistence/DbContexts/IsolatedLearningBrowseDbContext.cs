using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

internal sealed class IsolatedLearningBrowseDbContext(DbContextOptions<IsolatedLearningBrowseDbContext> options) : DbContext(options)
{
    internal const string Schema = "andril_learning_browse_isolated";
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        var row = model.Entity<LearningBrowseRow>();
        row.ToTable("discovery_evidence", t =>
        {
            t.HasCheckConstraint("ck_learning_browse_module", "module='learning' AND kind IN ('People','Classroom')");
            t.HasCheckConstraint("ck_learning_browse_ids", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND target_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        row.HasKey(x => new { x.AccountId, x.ProfileId, x.Kind, x.TargetId });
        row.Property(x => x.Kind).HasMaxLength(16); row.Property(x => x.Module).HasMaxLength(16);
        foreach (var p in row.Metadata.GetProperties())
            p.SetColumnName(string.Concat(p.Name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}

/// <summary>Exact requester/content ordinary-purpose evidence, never an AI classification or client flag.</summary>
internal sealed class LearningBrowseRow
{
    public Guid AccountId { get; set; }
    public Guid ProfileId { get; set; }
    public string Module { get; set; } = "learning";
    public string Kind { get; set; } = "People";
    public Guid TargetId { get; set; }
    public string Name { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTimeOffset EvidenceAt { get; set; }
    public bool Active { get; set; }
    public bool Eligible { get; set; }
    public bool Visible { get; set; }
    public bool BlocksClear { get; set; }
    public bool OrdinaryNameDisclosure { get; set; }
    public bool Public { get; set; }
    public bool PrivateDiscoveryAllowed { get; set; }
    public bool MembershipAllowed { get; set; }
    public override string ToString() => "Private Learning browse evidence [content omitted]";
}
