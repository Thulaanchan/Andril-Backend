using Andril.Learning.Activities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

internal sealed class IsolatedLearningActivitiesDbContext(DbContextOptions<IsolatedLearningActivitiesDbContext> options) : DbContext(options)
{
    internal const string Schema = "andril_learning_activities_isolated";
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        var row = model.Entity<LearningActivityRow>();
        row.ToTable("activities", table =>
        {
            table.HasCheckConstraint("ck_learning_activity_scope", "module = 'learning' AND id <> '00000000-0000-0000-0000-000000000000'::uuid AND generation <> '00000000-0000-0000-0000-000000000000'::uuid AND version > 0");
            table.HasCheckConstraint("ck_learning_activity_kind", "kind BETWEEN 1 AND 7");
            table.HasCheckConstraint("ck_learning_connection_pair", "kind <> 7 OR (jsonb_array_length(members) = 2 AND members->0->>'ProfileId' IS NOT NULL AND members->1->>'ProfileId' IS NOT NULL AND pair_low IS NOT NULL AND pair_high IS NOT NULL AND pair_low < pair_high AND pair_low = LEAST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND pair_high = GREATEST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND status IS NOT NULL AND status IN ('Pending','Accepted','Declined','Cancelled','Disconnected'))");
            table.HasCheckConstraint("ck_learning_activity_json", "jsonb_typeof(members) = 'array' AND jsonb_array_length(members) > 0 AND jsonb_typeof(content) = 'object'");
            table.HasCheckConstraint("ck_learning_exchange_pair", "kind <> 1 OR (jsonb_array_length(members) = 2 AND members->0->>'ProfileId' IS NOT NULL AND members->1->>'ProfileId' IS NOT NULL AND pair_low IS NOT NULL AND pair_high IS NOT NULL AND pair_low < pair_high AND pair_low = LEAST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND pair_high = GREATEST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND status IS NOT NULL AND status IN ('Pending','Accepted','Declined','Cancelled'))");
        });
        row.HasKey(x => x.Id); row.Property(x => x.Id).ValueGeneratedNever();
        row.Property(x => x.Module).HasMaxLength(16); row.Property(x => x.Kind).HasConversion<int>();
        row.Property(x => x.Status).HasMaxLength(32);
        row.Property(x => x.Members).HasColumnType("jsonb"); row.Property(x => x.Content).HasColumnType("jsonb");
        row.Property(x => x.Generation).IsConcurrencyToken(); row.Property(x => x.Version).IsConcurrencyToken();
        row.HasIndex(x => new { x.PairLow, x.PairHigh }).IsUnique()
            .HasFilter("kind = 1 AND status = 'Pending'").HasDatabaseName("ux_learning_activity_pending_exchange_pair");
        // A separate index avoids changing DEC-M1-01 or conflating connections with exchanges.
        row.HasIndex(x => new { x.Kind, x.PairLow, x.PairHigh }).IsUnique()
            .HasFilter("kind = 7 AND status IN ('Pending','Accepted')").HasDatabaseName("ux_learning_connection_live_pair");
        foreach (var property in row.Metadata.GetProperties())
            property.SetColumnName(string.Concat(property.Name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
    }
}

internal sealed class LearningActivityRow
{
    public Guid Id { get; set; }
    public string Module { get; set; } = "learning";
    public LearningActivityKind Kind { get; set; }
    public Guid Generation { get; set; }
    public long Version { get; set; }
    public string? Status { get; set; }
    public Guid? PairLow { get; set; }
    public Guid? PairHigh { get; set; }
    public string Members { get; set; } = "[]";
    public string Content { get; set; } = "{}";
    public override string ToString() => "Private Learning activity row [content omitted]";
}
