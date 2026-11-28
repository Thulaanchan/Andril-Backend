using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class LearningPublicationsDbContext(DbContextOptions<LearningPublicationsDbContext> options):DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder builder)=>builder.UseNpgsql(o=>o.MigrationsHistoryTable("__EFMigrationsHistory_LearningPublications","andril_learning"));
    protected override void OnModelCreating(ModelBuilder model)=>LearningPublicationsModelV1.Configure(model);
}
internal sealed class LearningPublicationRow
{
    public Guid AccountId{get;set;}public Guid ProfileId{get;set;}public string Module{get;set;}="learning";
    public int Kind{get;set;}public Guid ResourceId{get;set;}public Guid Generation{get;set;}public long Version{get;set;}
    public bool Published{get;set;}public bool NameDisclosure{get;set;}
    public long LifecycleRevision{get;set;}public string? ReviewedName{get;set;}
}
internal static class LearningPublicationsModelV1
{
    internal const string Check="module='learning' AND kind IN (1,2) AND account_id<>'00000000-0000-0000-0000-000000000000'::uuid AND profile_id<>'00000000-0000-0000-0000-000000000000'::uuid AND resource_id<>'00000000-0000-0000-0000-000000000000'::uuid AND generation<>'00000000-0000-0000-0000-000000000000'::uuid AND version>0 AND (kind<>1 OR resource_id=profile_id) AND (kind<>2 OR NOT name_disclosure)";
    internal static void Configure(ModelBuilder model)
    {
        model.HasDefaultSchema("andril_learning");var row=model.Entity<LearningPublicationRow>();
        row.ToTable("publications","andril_learning",t=>t.HasCheckConstraint("ck_learning_publication",Check));
        row.HasKey(x=>new{x.ProfileId,x.Kind,x.ResourceId}).HasName("pk_learning_publications");
        row.Property(x=>x.AccountId).HasColumnName("account_id");row.Property(x=>x.ProfileId).HasColumnName("profile_id").ValueGeneratedNever();
        row.Property(x=>x.Kind).HasColumnName("kind");row.Property(x=>x.ResourceId).HasColumnName("resource_id").ValueGeneratedNever();
        row.Property(x=>x.Module).HasColumnName("module").HasMaxLength(16).IsRequired();row.Property(x=>x.Generation).HasColumnName("generation").ValueGeneratedNever();
        row.Property(x=>x.Version).HasColumnName("version").IsConcurrencyToken();row.Property(x=>x.Published).HasColumnName("published");row.Property(x=>x.NameDisclosure).HasColumnName("name_disclosure");
        row.Property(x=>x.LifecycleRevision).HasColumnName("lifecycle_revision");row.Property(x=>x.ReviewedName).HasColumnName("reviewed_name").IsRequired(false);
    }
}
