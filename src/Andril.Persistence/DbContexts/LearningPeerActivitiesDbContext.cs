using Andril.Learning.Activities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>Participant-only Exchanges, Pacts and Connections; no discovery or offer publication.</summary>
public sealed class LearningPeerActivitiesDbContext(DbContextOptions<LearningPeerActivitiesDbContext> options):DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder builder)=>builder.UseNpgsql(o=>o.MigrationsHistoryTable("__EFMigrationsHistory_LearningPeers","andril_learning"));
    protected override void OnModelCreating(ModelBuilder model)=>LearningPeerActivitiesModelV2.Configure(model);
}

internal static class LearningPeerActivitiesModelV2
{
    internal const string Scope="module='learning' AND kind IN (1,2,5,7) AND id<>'00000000-0000-0000-0000-000000000000'::uuid AND generation<>'00000000-0000-0000-0000-000000000000'::uuid AND version>0";
    internal static void Configure(ModelBuilder model)
    {
        LearningPeerActivitiesModelV1.Configure(model);
        var row=model.Entity<LearningActivityRow>();
        row.ToTable("peer_activities","andril_learning",t=>t.HasCheckConstraint("ck_learning_peer_scope",Scope));
        row.HasIndex(x=>new{x.PairLow,x.PairHigh,x.Kind}).IsUnique().HasFilter("kind=5 AND status='Pending'").HasDatabaseName("ux_learning_peer_mentorship_pending");
    }
}

internal static class LearningPeerActivitiesModelV1
{
    internal const string Scope="module='learning' AND kind IN (1,2,7) AND id<>'00000000-0000-0000-0000-000000000000'::uuid AND generation<>'00000000-0000-0000-0000-000000000000'::uuid AND version>0";
    internal const string Members="COALESCE((jsonb_typeof(members)='array' AND jsonb_array_length(members)>=2 AND (kind=2 OR jsonb_array_length(members)=2) AND CASE WHEN jsonb_array_length(members)=2 THEN pair_low IS NOT NULL AND pair_high IS NOT NULL AND pair_low<pair_high AND pair_low=LEAST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) AND pair_high=GREATEST((members->0->>'ProfileId')::uuid,(members->1->>'ProfileId')::uuid) ELSE pair_low IS NULL AND pair_high IS NULL END AND content->'Members'=members AND content->>'Id'=id::text AND content->>'Kind'=kind::text AND members @> jsonb_build_array(content->'Creator')),false)";
    internal static void Configure(ModelBuilder model)
    {
        model.HasDefaultSchema("andril_learning");var row=model.Entity<LearningActivityRow>();
        row.ToTable("peer_activities","andril_learning",t=>{t.HasCheckConstraint("ck_learning_peer_scope",Scope);t.HasCheckConstraint("ck_learning_peer_members",Members);});
        row.HasKey(x=>x.Id).HasName("pk_learning_peer_activities");row.Property(x=>x.Id).ValueGeneratedNever();
        row.Property(x=>x.Generation).ValueGeneratedNever();
        row.Property(x=>x.Module).HasMaxLength(16).IsRequired();row.Property(x=>x.Kind).HasConversion<int>();row.Property(x=>x.Status).HasMaxLength(32).IsRequired(false);
        row.Property(x=>x.Members).HasColumnType("jsonb").IsRequired();row.Property(x=>x.Content).HasColumnType("jsonb").IsRequired();row.Property(x=>x.Version).IsConcurrencyToken();
        row.HasIndex(x=>new{x.PairLow,x.PairHigh}).IsUnique().HasFilter("kind=1 AND status='Pending'").HasDatabaseName("ux_learning_peer_exchange_pending");
        row.HasIndex(x=>new{x.Kind,x.PairLow,x.PairHigh}).IsUnique().HasFilter("kind=7 AND status IN ('Pending','Accepted')").HasDatabaseName("ux_learning_peer_connection_live");
        foreach(var property in row.Metadata.GetProperties())property.SetColumnName(string.Concat(property.Name.Select((c,i)=>i>0&&char.IsUpper(c)?"_"+char.ToLowerInvariant(c):char.ToLowerInvariant(c).ToString())));
    }
}
